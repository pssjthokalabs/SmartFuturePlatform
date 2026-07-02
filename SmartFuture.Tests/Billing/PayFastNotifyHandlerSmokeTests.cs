using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-6 handler smoke tests for PayFastNotifyHandler after its
// rewire to delegate to PayFastNotifyDecisionCore. Each test proves
// the handler's contract with the outside world via mocks:
//
//   - Valid COMPLETE ITN → IPaymentApplierService called exactly
//     once with NewStatus=Completed.
//   - Invalid signature / invalid merchant / unknown reference /
//     amount mismatch → the applier is NEVER called.
//
// PayFast doesn't have a verify service, so this test suite is
// simpler than the Paystack counterpart. Auto-billing reconcile is
// Loose-mocked. Order-intent service is Loose (unused unless the
// intent-first branch fires).
public class PayFastNotifyHandlerSmokeTests
{
    private const string MerchantId = "10000100";
    private const string Passphrase = "test-passphrase";

    private sealed class HandlerHarness
    {
        public required SqliteTestDbFixture Fixture { get; init; }
        public required PayFastNotifyHandler Handler { get; init; }
        public required Mock<IPaymentApplierService> Applier { get; init; }
        public required Mock<ICustomerPaymentMandateService> Mandates { get; init; }
    }

    private static async Task<HandlerHarness> BuildAsync(
        bool tokenizationEnabled = false,
        bool tokenizationForInvoicePayments = false,
        bool tokenizationForOrderIntents = false)
    {
        var fx = await SqliteTestDbFixture.CreateAsync();

        var applier = new Mock<IPaymentApplierService>(MockBehavior.Loose);
        applier.Setup(x => x.ApplyStatusChangeAsync(
                It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PaymentDto>.Success(new PaymentDto(), "applied"));

        var mandates = new Mock<ICustomerPaymentMandateService>(MockBehavior.Loose);
        mandates.Setup(x => x.UpsertPayFastMandateAsync(
                It.IsAny<UpsertPayFastMandateRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Guid>.Success(Guid.NewGuid(), "ok"));
        var orderIntent = new Mock<IOrderIntentService>(MockBehavior.Loose);
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Loose);

        var settings = new PayFastSettings
        {
            Enabled = true,
            MerchantId = MerchantId,
            MerchantKey = "test-key",
            Passphrase = Passphrase,
            NotifyUrl = "https://uatapi.smartfuture.co.za/api/payments/payfast/notify",
            TokenizationEnabled = tokenizationEnabled,
            TokenizationForInvoicePaymentsEnabled = tokenizationForInvoicePayments,
            TokenizationForOrderIntentsEnabled = tokenizationForOrderIntents,
        };

        var handler = new PayFastNotifyHandler(
            fx.AppDbContext,
            applier.Object,
            orderIntent.Object,
            mandates.Object,
            autoBilling.Object,
            Options.Create(settings),
            NullLogger<PayFastNotifyHandler>.Instance);

        return new HandlerHarness { Fixture = fx, Handler = handler, Applier = applier, Mandates = mandates };
    }

    private static async Task<(User user, PaymentInitiation initiation)> SeedInitiationAsync(
        SqliteTestDbFixture fx,
        decimal amount = 349.77m,
        string reference = "PF-REF-1",
        PaymentStatus paymentStatus = PaymentStatus.Pending)
    {
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext, type: ServicePackageType.Fibre);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg);
        await fx.DbContext.SaveChangesAsync();

        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order,
            totalAmount: amount,
            dueAtUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        TestEntityFactory.AddLineItem(fx.AppDbContext, invoice, InvoiceLineItemType.InstallationFee, amount, "Test");
        await fx.DbContext.SaveChangesAsync();

        var payment = new Payment
        {
            PaymentNumber = $"PAY-{Guid.NewGuid():N}"[..15],
            InvoiceId = invoice.Id,
            Invoice = invoice,
            Status = paymentStatus,
            Amount = amount,
            CurrencyCode = "ZAR",
            GatewayName = "PayFast",
        };
        fx.DbContext.Payments.Add(payment);
        await fx.DbContext.SaveChangesAsync();

        var initiation = new PaymentInitiation
        {
            InvoiceId = invoice.Id,
            Invoice = invoice,
            PaymentId = payment.Id,
            Payment = payment,
            Provider = PaymentProviderType.PayFast,
            Status = PaymentInitiationStatus.Pending,
            Amount = amount,
            CurrencyCode = "ZAR",
            ProviderReference = reference,
            WebhookApplyMode = WebhookApplyMode.ApplyNormally,
        };
        fx.DbContext.PaymentInitiations.Add(initiation);
        await fx.DbContext.SaveChangesAsync();
        return (user, initiation);
    }

    // Build a valid PayFast ITN payload + posted-fields list with a
    // signature computed against the ITN algorithm. Overrides let each
    // test twist a field to trigger the corresponding reject branch.
    private static (PayFastNotifyPayload payload, IReadOnlyList<KeyValuePair<string, string>> posted) BuildValidItn(
        string reference = "PF-REF-1",
        decimal amountGross = 349.77m,
        string paymentStatus = "COMPLETE",
        string merchantId = MerchantId,
        string passphrase = Passphrase,
        bool tamperSignature = false,
        string? token = null)
    {
        var posted = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id",   reference),
            new("pf_payment_id",  "PF-987654"),
            new("payment_status", paymentStatus),
            new("amount_gross",   PayFastSignatureCalculator.FormatAmount(amountGross)),
            new("amount_fee",     "0.00"),
            new("amount_net",     PayFastSignatureCalculator.FormatAmount(amountGross)),
            new("merchant_id",    merchantId),
        };
        var computed = PayFastSignatureCalculator.GenerateItnSignature(posted, passphrase, out _);
        var payload = new PayFastNotifyPayload
        {
            MPaymentId = reference,
            PfPaymentId = "PF-987654",
            PaymentStatus = paymentStatus,
            AmountGross = amountGross,
            AmountFee = 0m,
            AmountNet = amountGross,
            MerchantId = merchantId,
            Signature = tamperSignature ? "0000000000000000000000000000000000000000000000000000000000000000" : computed,
            Token = token,
        };
        posted.Add(new KeyValuePair<string, string>("signature", payload.Signature!));
        return (payload, posted);
    }

    // ─── Happy path ────────────────────────────────────────────────

    [Fact]
    public async Task PayFastNotifyHandler_ValidPaidItn_CallsPaymentApplierOnce()
    {
        var h = await BuildAsync();
        var (_, initiation) = await SeedInitiationAsync(h.Fixture, amount: 349.77m, reference: "PF-REF-1");
        var (payload, posted) = BuildValidItn("PF-REF-1", 349.77m);

        var outcome = await h.Handler.HandleAsync(payload, posted, CancellationToken.None);

        outcome.Accepted.Should().BeTrue(outcome.Message);
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.Is<ApplyPaymentStatusChangeRequestDto>(r =>
                r.PaymentId == initiation.PaymentId!.Value
                && r.NewStatus == PaymentStatus.Completed),
            It.IsAny<CancellationToken>()), Times.Once);

        await h.Fixture.DisposeAsync();
    }

    // ─── Invalid signature ─────────────────────────────────────────

    [Fact]
    public async Task PayFastNotifyHandler_InvalidSignature_DoesNotCallPaymentApplier()
    {
        var h = await BuildAsync();
        await SeedInitiationAsync(h.Fixture);
        var (payload, posted) = BuildValidItn(tamperSignature: true);

        var outcome = await h.Handler.HandleAsync(payload, posted, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Message.Should().Be("Signature mismatch");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.Fixture.DisposeAsync();
    }

    // ─── Invalid merchant ─────────────────────────────────────────

    [Fact]
    public async Task PayFastNotifyHandler_InvalidMerchant_DoesNotCallPaymentApplier()
    {
        var h = await BuildAsync();
        await SeedInitiationAsync(h.Fixture);
        var (payload, posted) = BuildValidItn(merchantId: "99999999");

        var outcome = await h.Handler.HandleAsync(payload, posted, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Message.Should().Be("Merchant ID mismatch");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.Fixture.DisposeAsync();
    }

    // ─── Unknown reference ────────────────────────────────────────

    [Fact]
    public async Task PayFastNotifyHandler_UnknownReference_DoesNotCallPaymentApplier()
    {
        var h = await BuildAsync();
        // Nothing seeded for PF-REF-UNKNOWN.
        var (payload, posted) = BuildValidItn("PF-REF-UNKNOWN", 349.77m);

        var outcome = await h.Handler.HandleAsync(payload, posted, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Message.Should().Be("Unknown reference");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.Fixture.DisposeAsync();
    }

    // ─── Amount mismatch ─────────────────────────────────────────

    [Fact]
    public async Task PayFastNotifyHandler_AmountMismatch_DoesNotCallPaymentApplier()
    {
        var h = await BuildAsync();
        await SeedInitiationAsync(h.Fixture, amount: 349.77m, reference: "PF-REF-1");
        // ITN reports 999.99 for the same reference — big mismatch.
        var (payload, posted) = BuildValidItn("PF-REF-1", 999.99m);

        var outcome = await h.Handler.HandleAsync(payload, posted, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Message.Should().Be("Amount mismatch");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.Fixture.DisposeAsync();
    }

    // ─── Drift guard: handler rejection mirrors the core ────────

    [Fact]
    public async Task PayFastNotifyHandler_RejectionReason_MatchesDecisionCore()
    {
        var h = await BuildAsync();
        await SeedInitiationAsync(h.Fixture, amount: 349.77m, reference: "PF-REF-1");
        var (payload, posted) = BuildValidItn("PF-REF-1", 999.99m);   // amount-mismatch

        var outcome = await h.Handler.HandleAsync(payload, posted, CancellationToken.None);

        outcome.Message.Should().Be(PayFastNotifyDecisionCore.RejectMessages.AmountMismatch);

        await h.Fixture.DisposeAsync();
    }

    // ─── TryCapturePayFastTokenAsync — indirect coverage ────────

    [Fact]
    public async Task PayFastNotifyHandler_CompleteItnWithToken_TokenizationEnabled_CapturesPayFastMandate()
    {
        // Invoice-bound COMPLETE ITN carrying a token + tokenization
        // enabled for invoice payments → mandate service called once
        // with UserId = order.UserId, Token from payload,
        // ConsentSource = InstallationCheckout, AutoEnableAutoBilling = true.
        var h = await BuildAsync(tokenizationEnabled: true, tokenizationForInvoicePayments: true);
        var (user, _) = await SeedInitiationAsync(h.Fixture, amount: 349.77m, reference: "PF-REF-1");
        var (payload, posted) = BuildValidItn("PF-REF-1", 349.77m, token: "TOKEN-ABC");

        var outcome = await h.Handler.HandleAsync(payload, posted, CancellationToken.None);
        outcome.Accepted.Should().BeTrue(outcome.Message);

        h.Mandates.Verify(x => x.UpsertPayFastMandateAsync(
            It.Is<UpsertPayFastMandateRequestDto>(r =>
                r.UserId == user.Id
                && r.Token == "TOKEN-ABC"
                && r.AutoEnableAutoBilling == true
                && r.ConsentSource == SmartFuture.Shared.Enums.Billing.CustomerMandateConsentSource.InstallationCheckout),
            It.IsAny<CancellationToken>()), Times.Once);

        await h.Fixture.DisposeAsync();
    }

    [Fact]
    public async Task PayFastNotifyHandler_CompleteItn_NoTokenInPayload_DoesNotCapture()
    {
        var h = await BuildAsync(tokenizationEnabled: true, tokenizationForInvoicePayments: true);
        await SeedInitiationAsync(h.Fixture, amount: 349.77m, reference: "PF-REF-1");
        var (payload, posted) = BuildValidItn("PF-REF-1", 349.77m, token: null);

        await h.Handler.HandleAsync(payload, posted, CancellationToken.None);

        h.Mandates.Verify(x => x.UpsertPayFastMandateAsync(
            It.IsAny<UpsertPayFastMandateRequestDto>(), It.IsAny<CancellationToken>()), Times.Never,
            "no token in ITN → nothing to capture");

        await h.Fixture.DisposeAsync();
    }

    [Fact]
    public async Task PayFastNotifyHandler_CompleteItn_TokenizationDisabled_DoesNotCapture()
    {
        var h = await BuildAsync(tokenizationEnabled: false);
        await SeedInitiationAsync(h.Fixture, amount: 349.77m, reference: "PF-REF-1");
        var (payload, posted) = BuildValidItn("PF-REF-1", 349.77m, token: "TOKEN-ABC");

        await h.Handler.HandleAsync(payload, posted, CancellationToken.None);

        h.Mandates.Verify(x => x.UpsertPayFastMandateAsync(
            It.IsAny<UpsertPayFastMandateRequestDto>(), It.IsAny<CancellationToken>()), Times.Never,
            "master TokenizationEnabled=false must gate every capture");

        await h.Fixture.DisposeAsync();
    }

    [Fact]
    public async Task PayFastNotifyHandler_TokenCaptureThrows_PaymentSettlementStillSucceeds()
    {
        // Token capture is best-effort — a thrown exception inside
        // TryCapturePayFastTokenAsync must NOT break the settlement.
        var h = await BuildAsync(tokenizationEnabled: true, tokenizationForInvoicePayments: true);
        h.Mandates.Setup(x => x.UpsertPayFastMandateAsync(
                It.IsAny<UpsertPayFastMandateRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mandate service down"));
        var (_, initiation) = await SeedInitiationAsync(h.Fixture, amount: 349.77m, reference: "PF-REF-1");
        var (payload, posted) = BuildValidItn("PF-REF-1", 349.77m, token: "TOKEN-ABC");

        var outcome = await h.Handler.HandleAsync(payload, posted, CancellationToken.None);

        outcome.Accepted.Should().BeTrue(
            "token capture failure must NEVER break the payment path — it's a best-effort side-channel");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.Is<ApplyPaymentStatusChangeRequestDto>(r => r.PaymentId == initiation.PaymentId!.Value),
            It.IsAny<CancellationToken>()), Times.Once);

        await h.Fixture.DisposeAsync();
    }
}

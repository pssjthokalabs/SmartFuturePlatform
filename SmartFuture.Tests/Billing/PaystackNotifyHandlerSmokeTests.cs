using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-6 handler smoke tests for PaystackNotifyHandler after its
// rewire to delegate to PaystackNotifyDecisionCore. Each test proves
// the handler's contract with the outside world via the mocks:
//
//   - Valid charge.success webhook → IPaymentApplierService is called
//     exactly once with NewStatus=Completed.
//   - Invalid signature / unknown reference / amount mismatch → the
//     applier is NEVER called.
//   - Already-completed (duplicate delivery) → the applier is NEVER
//     called; response is 200 acknowledgement.
//
// The verify service is mocked to return Success with matching values
// so the belt-and-braces gate agrees on success paths. The mandate
// upsert service is Loose (never fails). The order-intent service is
// Loose (unused unless the SF-INTENT-* branch fires).
public class PaystackNotifyHandlerSmokeTests
{
    private const string SecretKey = "sk_test_XXXXXXXXXXXXXXXXXXXXXXXX";

    private sealed class HandlerHarness
    {
        public required SqliteTestDbFixture Fixture { get; init; }
        public required PaystackNotifyHandler Handler { get; init; }
        public required Mock<IPaymentApplierService> Applier { get; init; }
        public required Mock<IOrderIntentService> OrderIntent { get; init; }
        public required Mock<ICustomerPaymentMandateService> Mandates { get; init; }
        public required RecordingHandler HttpHandler { get; init; }
    }

    private static string ComputeSignature(string body, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash);
    }

    private static async Task<HandlerHarness> BuildAsync(string paystackVerifyStatus = "success", long paystackVerifyAmount = 99900)
    {
        var fx = await SqliteTestDbFixture.CreateAsync();

        var applier = new Mock<IPaymentApplierService>(MockBehavior.Loose);
        applier.Setup(x => x.ApplyStatusChangeAsync(
                It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PaymentDto>.Success(new PaymentDto(), "applied"));

        var mandates = new Mock<ICustomerPaymentMandateService>(MockBehavior.Loose);
        mandates.Setup(x => x.UpsertPaystackMandateAsync(
                It.IsAny<UpsertPaystackMandateRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Guid>.Success(Guid.NewGuid(), "ok"));

        var orderIntent = new Mock<IOrderIntentService>(MockBehavior.Loose);

        var env = new Mock<IHostEnvironment>(MockBehavior.Loose);
        env.SetupGet(x => x.EnvironmentName).Returns("Development");

        // Fake verify-service HTTP so the handler's verify call
        // never leaves the process. Default: return a Paystack "success"
        // that agrees with the webhook payload.
        var verifyBody = "{\"status\":true,\"message\":\"ok\",\"data\":{\"id\":1,\"status\":\"" + paystackVerifyStatus + "\",\"reference\":\"REF-1\",\"amount\":" + paystackVerifyAmount + ",\"currency\":\"ZAR\"}}";
        var httpHandler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(verifyBody, Encoding.UTF8, "application/json"),
        }));
        var httpClient = new HttpClient(httpHandler);

        var settings = new PaystackSettings
        {
            Enabled = true,
            SecretKey = SecretKey,
            PublicKey = "pk_test_XX",
            CallbackUrl = "https://uatapi.smartfuture.co.za/api/paystack/callback",
            VerifyBaseUrl = "https://api.paystack.co/transaction/verify",
            Currency = "ZAR",
        };
        var verifier = new PaystackVerificationService(
            httpClient, Options.Create(settings), NullLogger<PaystackVerificationService>.Instance);

        var handler = new PaystackNotifyHandler(
            fx.AppDbContext,
            applier.Object,
            Options.Create(settings),
            Options.Create(new PaymentProcessingSettings { WebhookApplyEnabled = true }),
            verifier,
            mandates.Object,
            env.Object,
            orderIntent.Object,
            NullLogger<PaystackNotifyHandler>.Instance);

        return new HandlerHarness
        {
            Fixture = fx, Handler = handler, Applier = applier, OrderIntent = orderIntent,
            Mandates = mandates, HttpHandler = httpHandler,
        };
    }

    private static async Task<(User user, PaymentInitiation initiation)> SeedInitiationAsync(
        SqliteTestDbFixture fx,
        decimal amount = 999m,
        string reference = "REF-1",
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
            GatewayName = "Paystack",
        };
        fx.DbContext.Payments.Add(payment);
        await fx.DbContext.SaveChangesAsync();

        var initiation = new PaymentInitiation
        {
            InvoiceId = invoice.Id,
            Invoice = invoice,
            PaymentId = payment.Id,
            Payment = payment,
            Provider = PaymentProviderType.Paystack,
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

    private static string BuildChargeSuccessBody(string reference = "REF-1", long amount = 99900, string status = "success")
        => "{\"event\":\"charge.success\",\"data\":{\"id\":1234,\"status\":\"" + status + "\",\"reference\":\"" + reference + "\",\"amount\":" + amount + ",\"currency\":\"ZAR\"}}";

    private static string BuildChargeSuccessBodyWithAuthorization(
        string reference = "REF-1",
        long amount = 99900,
        bool reusable = true,
        string? authorizationCode = "AUTH_ABC",
        string? customerEmail = "customer@example.com",
        string? customerCode = "CUS_XYZ")
    {
        var authBlock = authorizationCode is null
            ? "null"
            : "{\"authorization_code\":\"" + authorizationCode + "\",\"reusable\":" + reusable.ToString().ToLowerInvariant()
                + ",\"channel\":\"card\",\"card_type\":\"visa\",\"last4\":\"4242\",\"exp_month\":\"12\",\"exp_year\":\"2030\",\"bank\":\"TEST\",\"account_name\":\"Test\",\"signature\":\"sig\"}";
        var customerBlock = customerCode is null
            ? "null"
            : "{\"email\":\"" + customerEmail + "\",\"customer_code\":\"" + customerCode + "\"}";
        return "{\"event\":\"charge.success\",\"data\":{\"id\":1234,\"status\":\"success\",\"reference\":\""
             + reference + "\",\"amount\":" + amount + ",\"currency\":\"ZAR\",\"authorization\":" + authBlock
             + ",\"customer\":" + customerBlock + "}}";
    }

    // ─── Happy path ────────────────────────────────────────────────

    [Fact]
    public async Task PaystackNotifyHandler_ValidSuccess_CallsPaymentApplierOnce()
    {
        var h = await BuildAsync();
        var (_, initiation) = await SeedInitiationAsync(h.Fixture, amount: 999m, reference: "REF-1");
        var body = BuildChargeSuccessBody("REF-1", 99900);
        var signature = ComputeSignature(body, SecretKey);

        var outcome = await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        outcome.Accepted.Should().BeTrue(outcome.Message);
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.Is<ApplyPaymentStatusChangeRequestDto>(r =>
                r.PaymentId == initiation.PaymentId!.Value
                && r.NewStatus == PaymentStatus.Completed),
            It.IsAny<CancellationToken>()), Times.Once);

        await h.Fixture.DisposeAsync();
    }

    // ─── Signature ────────────────────────────────────────────────

    [Fact]
    public async Task PaystackNotifyHandler_InvalidSignature_DoesNotCallPaymentApplier()
    {
        var h = await BuildAsync();
        await SeedInitiationAsync(h.Fixture);
        var body = BuildChargeSuccessBody();
        var badSignature = ComputeSignature(body, "wrong-secret-XXX");

        var outcome = await h.Handler.HandleAsync(body, badSignature, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Message.Should().Be("Signature mismatch");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.Fixture.DisposeAsync();
    }

    // ─── Unknown reference ────────────────────────────────────────

    [Fact]
    public async Task PaystackNotifyHandler_UnknownReference_DoesNotCallPaymentApplier()
    {
        var h = await BuildAsync();
        // No initiation seeded for reference "REF-UNKNOWN".
        var body = BuildChargeSuccessBody("REF-UNKNOWN", 99900);
        var signature = ComputeSignature(body, SecretKey);

        var outcome = await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Message.Should().Be("Unknown reference");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.Fixture.DisposeAsync();
    }

    // ─── Already completed (duplicate delivery) ──────────────────

    [Fact]
    public async Task PaystackNotifyHandler_AlreadyCompleted_NoOpsWithoutApply()
    {
        var h = await BuildAsync();
        // Seed a payment already in Completed status — mimics the
        // second delivery of the same Paystack webhook event.
        await SeedInitiationAsync(h.Fixture, paymentStatus: PaymentStatus.Completed);
        var body = BuildChargeSuccessBody();
        var signature = ComputeSignature(body, SecretKey);

        var outcome = await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        outcome.Accepted.Should().BeTrue("duplicate webhook is acknowledged, not rejected");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never,
            "already-Completed short-circuits BEFORE the applier is called");

        await h.Fixture.DisposeAsync();
    }

    // ─── Amount mismatch ─────────────────────────────────────────

    [Fact]
    public async Task PaystackNotifyHandler_AmountMismatch_DoesNotCallPaymentApplier()
    {
        var h = await BuildAsync();
        await SeedInitiationAsync(h.Fixture, amount: 999m, reference: "REF-1");
        // Same reference, wildly different amount from what the initiation records.
        var body = BuildChargeSuccessBody("REF-1", amount: 10_000L);
        var signature = ComputeSignature(body, SecretKey);

        var outcome = await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Message.Should().Be("Amount mismatch");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.Fixture.DisposeAsync();
    }

    // ─── Drift guard: handler rejection reason mirrors the core ─

    [Fact]
    public async Task PaystackNotifyHandler_RejectionReason_MatchesDecisionCore()
    {
        var h = await BuildAsync();
        // Force amount-mismatch — the core's RejectCode should surface
        // as the handler's Message via the RejectMessage lookup.
        await SeedInitiationAsync(h.Fixture, amount: 999m, reference: "REF-1");
        var body = BuildChargeSuccessBody("REF-1", amount: 10_000L);
        var signature = ComputeSignature(body, SecretKey);

        var outcome = await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        // The persisted webhook log row is the operator-facing signal;
        // assert its RejectionReason matches the RejectCode constant.
        var logRow = await h.Fixture.DbContext.PaystackWebhookLogs.AsNoTracking().SingleAsync();
        logRow.RejectionReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.AmountMismatch);
        logRow.Accepted.Should().BeFalse();

        await h.Fixture.DisposeAsync();
    }

    // ─── Verify-disagreement branch ──────────────────────────────

    [Fact]
    public async Task PaystackNotifyHandler_VerifyDisagreement_RejectsWithoutApply()
    {
        // Paystack's belt-and-braces verify call returns a status/amount
        // that DISAGREES with the signed webhook. Handler must reject
        // with reason "verify-disagreement" and NEVER call the applier.
        var h = await BuildAsync(paystackVerifyStatus: "success", paystackVerifyAmount: 10_000);
        // Webhook says amount 99900; verify service returns amount 10000 → disagreement.
        await SeedInitiationAsync(h.Fixture, amount: 999m, reference: "REF-1");
        var body = BuildChargeSuccessBody("REF-1", 99_900);
        var signature = ComputeSignature(body, SecretKey);

        var outcome = await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        outcome.Accepted.Should().BeFalse();
        outcome.Message.Should().Be("Verify disagreement");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never,
            "verify disagreement must short-circuit BEFORE the applier is called");

        var logRow = await h.Fixture.DbContext.PaystackWebhookLogs.AsNoTracking().SingleAsync();
        logRow.RejectionReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.VerifyDisagreement);

        await h.Fixture.DisposeAsync();
    }

    // ─── Intent-convert branch (SF-INTENT-* references) ─────────

    [Fact]
    public async Task PaystackNotifyHandler_IntentReference_CallsOrderIntentServiceOnce_AndSkipsApplier()
    {
        // SF-INTENT-* references bypass the PaymentInitiation lookup
        // and delegate to IOrderIntentService.ConvertIntentPaymentToPaidOrderAsync.
        // The applier is called INSIDE the convert path (Phase 4 covers
        // that); the webhook handler itself must NOT call the applier
        // directly on the intent branch.
        var h = await BuildAsync();
        h.OrderIntent
            .Setup(x => x.ConvertIntentPaymentToPaidOrderAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<PaystackVerifyAuthorizationSnapshot?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Success(
                new ConvertIntentPaymentToPaidOrderOutcomeDto
                {
                    Reference        = "SF-INTENT-abc123",
                    OrderIntentId    = Guid.NewGuid(),
                    OrderId          = Guid.NewGuid(),
                    OrderNumber      = "SF-2026-0001",
                    InvoiceId        = Guid.NewGuid(),
                    InvoiceNumber    = "INV-2026-0001",
                    PaymentId        = Guid.NewGuid(),
                    PaymentNumber    = "PAY-2026-0001",
                    AlreadyConverted = false,
                }, "converted"));

        var body = BuildChargeSuccessBody("SF-INTENT-abc123", 99_900);
        var signature = ComputeSignature(body, SecretKey);

        var outcome = await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        outcome.Accepted.Should().BeTrue(outcome.Message);
        outcome.Message.Should().Contain("converted to Order SF-2026-0001");

        h.OrderIntent.Verify(x => x.ConvertIntentPaymentToPaidOrderAsync(
            "SF-INTENT-abc123",
            It.IsAny<DateTime?>(),
            It.IsAny<string?>(),
            It.IsAny<PaystackVerifyAuthorizationSnapshot?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()), Times.Never,
            "intent branch delegates to convert; the handler itself does NOT call the applier");

        await h.Fixture.DisposeAsync();
    }

    // ─── TryUpsertPaystackMandateAsync — indirect coverage ──────

    [Fact]
    public async Task PaystackNotifyHandler_ReusableAuthorization_UpsertsMandateWithCorrectFields()
    {
        // charge.success with a reusable authorization block →
        // ICustomerPaymentMandateService.UpsertPaystackMandateAsync is
        // called ONCE with UserId resolved from Invoice.Order.UserId,
        // authorization fields mirrored from the webhook, IsReusable=true,
        // AutoEnableAutoBilling=true, ConsentSource=InstallationCheckout.
        var h = await BuildAsync();
        var (user, _) = await SeedInitiationAsync(h.Fixture, amount: 999m, reference: "REF-1");
        var body = BuildChargeSuccessBodyWithAuthorization(
            "REF-1", 99_900, reusable: true, authorizationCode: "AUTH_TEST",
            customerEmail: "cust@x.io", customerCode: "CUS_TEST");
        var signature = ComputeSignature(body, SecretKey);

        var outcome = await h.Handler.HandleAsync(body, signature, CancellationToken.None);
        outcome.Accepted.Should().BeTrue(outcome.Message);

        h.Mandates.Verify(x => x.UpsertPaystackMandateAsync(
            It.Is<UpsertPaystackMandateRequestDto>(r =>
                r.UserId == user.Id
                && r.AuthorizationCode == "AUTH_TEST"
                && r.IsReusable == true
                && r.AutoEnableAutoBilling == true
                && r.CustomerEmail == "cust@x.io"
                && r.ProviderCustomerCode == "CUS_TEST"
                && r.ConsentSource == SmartFuture.Shared.Enums.Billing.CustomerMandateConsentSource.InstallationCheckout),
            It.IsAny<CancellationToken>()), Times.Once);

        await h.Fixture.DisposeAsync();
    }

    [Fact]
    public async Task PaystackNotifyHandler_NonReusableAuthorization_DoesNotUpsertMandate()
    {
        var h = await BuildAsync();
        await SeedInitiationAsync(h.Fixture);
        var body = BuildChargeSuccessBodyWithAuthorization(reusable: false, authorizationCode: "AUTH_ONESHOT");
        var signature = ComputeSignature(body, SecretKey);

        await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        h.Mandates.Verify(x => x.UpsertPaystackMandateAsync(
            It.IsAny<UpsertPaystackMandateRequestDto>(), It.IsAny<CancellationToken>()), Times.Never,
            "non-reusable authorizations must NOT be stored as a mandate");

        await h.Fixture.DisposeAsync();
    }

    [Fact]
    public async Task PaystackNotifyHandler_NoAuthorizationBlock_DoesNotUpsertMandate()
    {
        var h = await BuildAsync();
        await SeedInitiationAsync(h.Fixture);
        // Uses the base body which omits the authorization block.
        var body = BuildChargeSuccessBody();
        var signature = ComputeSignature(body, SecretKey);

        await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        h.Mandates.Verify(x => x.UpsertPaystackMandateAsync(
            It.IsAny<UpsertPaystackMandateRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.Fixture.DisposeAsync();
    }

    [Fact]
    public async Task PaystackNotifyHandler_MandateUpsertThrows_PaymentSettlementStillSucceeds()
    {
        // Mandate upsert is best-effort — a thrown exception inside
        // TryUpsertPaystackMandateAsync must NOT prevent the applier
        // from settling the payment.
        var h = await BuildAsync();
        h.Mandates.Setup(x => x.UpsertPaystackMandateAsync(
                It.IsAny<UpsertPaystackMandateRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mandate service down"));
        var (_, initiation) = await SeedInitiationAsync(h.Fixture, amount: 999m, reference: "REF-1");
        var body = BuildChargeSuccessBodyWithAuthorization("REF-1", 99_900);
        var signature = ComputeSignature(body, SecretKey);

        var outcome = await h.Handler.HandleAsync(body, signature, CancellationToken.None);

        outcome.Accepted.Should().BeTrue(
            "mandate failure must NEVER break the payment path — it's a best-effort side-channel");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.Is<ApplyPaymentStatusChangeRequestDto>(r => r.PaymentId == initiation.PaymentId!.Value),
            It.IsAny<CancellationToken>()), Times.Once);

        await h.Fixture.DisposeAsync();
    }

    // ─── HTTP recording handler ──────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }
        public int CallCount { get; private set; }
        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            return _respond(request);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Ozow NEW-ORDER intent settlement. Ozow order-intents settle by webhook
// ONLY — there is no client-side verify the way Paystack has one — so
// OzowNotifyHandler is the single point where a paid intent becomes a
// real Order. These tests pin the routing + the reconciliation guards:
//
//   • "SF-INTENT-…" reference → intent path, NOT the invoice path
//     (the invoice path would log "unknown reference" and silently drop
//     a paid order on the floor).
//   • Complete → ConvertIntentPaymentToPaidOrderAsync called exactly once.
//   • Amount mismatch → conversion REFUSED (replay/tamper guard).
//   • Cancelled/abandoned/error → intent released, no order minted.
//   • Unknown intent reference → refused, never converted.
//   • Non-terminal status → intent untouched, no conversion.
//   • Invoice-shaped reference ("SF-PAY-…") → must NOT reach the intent
//     path, proving the two flows stay separated.
//
// The invoice-settlement path is deliberately not re-tested here; it is
// unchanged and already covered elsewhere.
public class OzowNotifyHandlerIntentTests
{
    private const string SiteCode   = "TEST-SITE";
    private const string PrivateKey = "test-private-key";

    private sealed class Harness
    {
        public required SqliteTestDbFixture Fixture { get; init; }
        public required OzowNotifyHandler Handler { get; init; }
        public required Mock<IOrderIntentService> Intents { get; init; }
        public required Mock<IPaymentApplierService> Applier { get; init; }
    }

    private static async Task<Harness> BuildAsync()
    {
        var fx = await SqliteTestDbFixture.CreateAsync();

        var applier = new Mock<IPaymentApplierService>(MockBehavior.Loose);
        applier.Setup(x => x.ApplyStatusChangeAsync(
                It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PaymentDto>.Success(new PaymentDto(), "applied"));

        var intents = new Mock<IOrderIntentService>(MockBehavior.Loose);
        intents.Setup(x => x.ConvertIntentPaymentToPaidOrderAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<PaystackVerifyAuthorizationSnapshot?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Success(
                new ConvertIntentPaymentToPaidOrderOutcomeDto { OrderNumber = "SO-0001" }, "converted"));

        var settings = Options.Create(new OzowSettings
        {
            Enabled      = true,
            SiteCode     = SiteCode,
            ApiKey       = "api-key",
            PrivateKey   = PrivateKey,
            IsTest       = true,
            NotifyUrl    = "https://api.example.com/api/webhooks/ozow",
            SuccessUrl   = "https://portal.example.com/payment/result",
            CancelUrl    = "https://portal.example.com/payment/result",
            ErrorUrl     = "https://portal.example.com/payment/result",
            CurrencyCode = "ZAR",
            CountryCode  = "ZA"
        });

        var handler = new OzowNotifyHandler(
            fx.AppDbContext, applier.Object, intents.Object, settings,
            NullLogger<OzowNotifyHandler>.Instance);

        return new Harness { Fixture = fx, Handler = handler, Intents = intents, Applier = applier };
    }

    // Seeds an OrderIntent carrying the Ozow reference + the amount we
    // told Ozow to charge (IntentPaymentAmount is post-override, which is
    // exactly what the notification echoes back).
    private static async Task<string> SeedIntentAsync(
        SqliteTestDbFixture fx,
        decimal intentPaymentAmount,
        string? reference = null)
    {
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg  = TestEntityFactory.CreateServicePackage(fx.AppDbContext, ServicePackageType.Fibre);
        await fx.DbContext.SaveChangesAsync();

        reference ??= $"SF-INTENT-{Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()}";

        var intent = TestEntityFactory.CreateOrderIntent(fx.AppDbContext, user, pkg,
            nowUtc: new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc));
        intent.IntentPaymentReference = reference;
        intent.IntentPaymentAmount    = intentPaymentAmount;
        intent.Provider               = PaymentProviderType.Ozow;
        intent.Status                 = OrderIntentStatus.Pending;
        await fx.DbContext.SaveChangesAsync();

        return reference;
    }

    // Builds a payload whose hash actually validates, so the tests
    // exercise the routing logic rather than the hash guard.
    private static OzowNotifyPayload SignedPayload(
        string reference, decimal amount, string status, string transactionId = "OZOW-TX-1")
    {
        var payload = new OzowNotifyPayload
        {
            SiteCode             = SiteCode,
            TransactionId        = transactionId,
            TransactionReference = reference,
            Amount               = amount,
            Status               = status,
            CurrencyCode         = "ZAR",
            IsTest               = true,
            StatusMessage        = null
        };
        payload.Hash = OzowHashCalculator.BuildResponseHash(
            siteCode:             payload.SiteCode!,
            transactionId:        payload.TransactionId!,
            transactionReference: payload.TransactionReference!,
            amount:               payload.Amount,
            status:               payload.Status!,
            optional1:            null, optional2: null, optional3: null,
            optional4:            null, optional5: null,
            currencyCode:         payload.CurrencyCode!,
            isTest:               payload.IsTest,
            statusMessage:        payload.StatusMessage,
            privateKey:           PrivateKey);
        return payload;
    }

    [Fact]
    public async Task Complete_intent_notify_converts_intent_exactly_once()
    {
        var h = await BuildAsync();
        var reference = await SeedIntentAsync(h.Fixture, 100m);

        var outcome = await h.Handler.HandleAsync(
            SignedPayload(reference, 100m, "Complete"), CancellationToken.None);

        Assert.True(outcome.Accepted);
        h.Intents.Verify(x => x.ConvertIntentPaymentToPaidOrderAsync(
            reference, It.IsAny<DateTime?>(), "OZOW-TX-1",
            It.IsAny<PaystackVerifyAuthorizationSnapshot?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // The invoice-settlement path must never fire for an intent.
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Amount_mismatch_refuses_to_convert()
    {
        var h = await BuildAsync();
        // We told Ozow R100; the notification claims R10.
        var reference = await SeedIntentAsync(h.Fixture, 100m);

        var outcome = await h.Handler.HandleAsync(
            SignedPayload(reference, 10m, "Complete"), CancellationToken.None);

        Assert.False(outcome.Accepted);
        h.Intents.Verify(x => x.ConvertIntentPaymentToPaidOrderAsync(
            It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
            It.IsAny<PaystackVerifyAuthorizationSnapshot?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Abandoned")]
    [InlineData("Error")]
    public async Task Non_complete_status_releases_intent_and_creates_no_order(string status)
    {
        var h = await BuildAsync();
        var reference = await SeedIntentAsync(h.Fixture, 100m);

        var outcome = await h.Handler.HandleAsync(
            SignedPayload(reference, 100m, status), CancellationToken.None);

        Assert.True(outcome.Accepted);
        h.Intents.Verify(x => x.ConvertIntentPaymentToPaidOrderAsync(
            It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
            It.IsAny<PaystackVerifyAuthorizationSnapshot?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var intent = await h.Fixture.AppDbContext.OrderIntents
            .FirstAsync(i => i.IntentPaymentReference == reference);
        Assert.Equal(OrderIntentStatus.Cancelled, intent.Status);
    }

    [Fact]
    public async Task Late_failure_notify_does_not_undo_a_converted_intent()
    {
        var h = await BuildAsync();
        var reference = await SeedIntentAsync(h.Fixture, 100m);

        var intent = await h.Fixture.AppDbContext.OrderIntents
            .FirstAsync(i => i.IntentPaymentReference == reference);
        intent.Status = OrderIntentStatus.ConvertedToOrder;
        await h.Fixture.DbContext.SaveChangesAsync();

        var outcome = await h.Handler.HandleAsync(
            SignedPayload(reference, 100m, "Cancelled"), CancellationToken.None);

        Assert.True(outcome.Accepted);
        var after = await h.Fixture.AppDbContext.OrderIntents
            .FirstAsync(i => i.IntentPaymentReference == reference);
        Assert.Equal(OrderIntentStatus.ConvertedToOrder, after.Status);
    }

    [Fact]
    public async Task Non_terminal_status_leaves_intent_pending()
    {
        var h = await BuildAsync();
        var reference = await SeedIntentAsync(h.Fixture, 100m);

        var outcome = await h.Handler.HandleAsync(
            SignedPayload(reference, 100m, "PendingInvestigation"), CancellationToken.None);

        Assert.True(outcome.Accepted);
        h.Intents.Verify(x => x.ConvertIntentPaymentToPaidOrderAsync(
            It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
            It.IsAny<PaystackVerifyAuthorizationSnapshot?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var intent = await h.Fixture.AppDbContext.OrderIntents
            .FirstAsync(i => i.IntentPaymentReference == reference);
        Assert.Equal(OrderIntentStatus.Pending, intent.Status);
    }

    [Fact]
    public async Task Unknown_intent_reference_is_refused_not_converted()
    {
        var h = await BuildAsync();
        await SeedIntentAsync(h.Fixture, 100m);

        var outcome = await h.Handler.HandleAsync(
            SignedPayload("SF-INTENT-DEADBEEF0000", 100m, "Complete"), CancellationToken.None);

        Assert.False(outcome.Accepted);
        h.Intents.Verify(x => x.ConvertIntentPaymentToPaidOrderAsync(
            It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
            It.IsAny<PaystackVerifyAuthorizationSnapshot?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Invoice_shaped_reference_never_reaches_the_intent_path()
    {
        var h = await BuildAsync();
        await SeedIntentAsync(h.Fixture, 100m);

        // "SF-{paymentNumber}" is the invoice flow's format. There is no
        // PaymentInitiation seeded, so the invoice path bails with
        // "Unknown reference" — the point is that it does NOT get routed
        // to the intent handler and mint an order.
        var outcome = await h.Handler.HandleAsync(
            SignedPayload("SF-PAY-0001", 100m, "Complete"), CancellationToken.None);

        Assert.False(outcome.Accepted);
        h.Intents.Verify(x => x.ConvertIntentPaymentToPaidOrderAsync(
            It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
            It.IsAny<PaystackVerifyAuthorizationSnapshot?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Bad_hash_is_rejected_before_any_intent_lookup()
    {
        var h = await BuildAsync();
        var reference = await SeedIntentAsync(h.Fixture, 100m);

        var payload = SignedPayload(reference, 100m, "Complete");
        payload.Hash = "not-the-right-hash";

        var outcome = await h.Handler.HandleAsync(payload, CancellationToken.None);

        Assert.False(outcome.Accepted);
        h.Intents.Verify(x => x.ConvertIntentPaymentToPaidOrderAsync(
            It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
            It.IsAny<PaystackVerifyAuthorizationSnapshot?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

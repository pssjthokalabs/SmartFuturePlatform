using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Webhooks;
using SmartFuture.Application.Webhooks.Dtos;
using SmartFuture.Shared.Enums.Webhooks;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-7 dedup tests for WebhookInboxService.
//
// Contract (from WebhookInboxService.cs:119-147):
//   1. If the incoming webhook's ProviderEventId (from request or parsed)
//      matches an existing row for the same ProviderName → the service
//      returns the ORIGINAL inbox as a success + does NOT persist a new
//      row + does NOT re-run the applier.
//   2. If no ProviderEventId matches, the IdempotencyKey is checked
//      the same way.
//   3. When neither matches, the service persists a fresh Received row
//      and proceeds to parse + dispatch.
//   4. Signature-invalid short-circuits BEFORE any dedup check and
//      writes a SignatureInvalid row.
public class WebhookInboxServiceTests
{
    private sealed record Harness(
        SqliteTestDbFixture Fixture,
        WebhookInboxService Service,
        Mock<IWebhookSignatureValidator> Signature,
        Mock<IWebhookPayloadParser> Parser,
        Mock<IPaymentApplierService> Applier);

    private static Harness Build(SqliteTestDbFixture fx, bool signatureValid = true)
    {
        var sig = new Mock<IWebhookSignatureValidator>(MockBehavior.Loose);
        sig.Setup(x => x.IsValidAsync(It.IsAny<PaymentWebhookRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signatureValid);

        var parser = new Mock<IWebhookPayloadParser>(MockBehavior.Loose);

        var audit = new Mock<IAuditService>(MockBehavior.Loose);

        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Loose);
        currentUser.SetupGet(x => x.UserId).Returns((Guid?)null);

        var applier = new Mock<IPaymentApplierService>(MockBehavior.Loose);

        var svc = new WebhookInboxService(
            fx.AppDbContext, sig.Object, parser.Object,
            audit.Object, currentUser.Object, applier.Object,
            NullLogger<WebhookInboxService>.Instance);

        return new Harness(fx, svc, sig, parser, applier);
    }

    private static PaymentWebhookRequestDto ValidRequest(
        string providerName = "Paystack",
        string? providerEventId = "evt-001",
        string? idempotencyKey = null,
        string rawPayload = "{\"event\":\"charge.success\"}") =>
        new()
        {
            ProviderName    = providerName,
            ProviderEventId = providerEventId,
            IdempotencyKey  = idempotencyKey,
            RawPayload      = rawPayload,
            SignatureHeader = "sig",
        };

    private static void ParserReturns(Mock<IWebhookPayloadParser> parser, ParsedPaymentWebhookDto? parsed) =>
        parser.Setup(x => x.ParsePaymentWebhookAsync(It.IsAny<PaymentWebhookRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsed is null
                ? Result<ParsedPaymentWebhookDto>.Failure("parse-failed", "no")
                : Result<ParsedPaymentWebhookDto>.Success(parsed, "ok"));

    // ─── Bad-request validations ───────────────────────────────────

    [Fact]
    public async Task WebhookInboxService_NullRequest_ReturnsBadRequest()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        var result = await h.Service.ReceivePaymentWebhookAsync(null!);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WebhookInboxService_MissingProviderName_ReturnsValidationError()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        var req = ValidRequest(providerName: "");
        var result = await h.Service.ReceivePaymentWebhookAsync(req);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WebhookInboxService_MissingRawPayload_ReturnsValidationError()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        var req = ValidRequest(rawPayload: "");
        var result = await h.Service.ReceivePaymentWebhookAsync(req);
        result.IsSuccess.Should().BeFalse();
    }

    // ─── Signature validation ─────────────────────────────────────

    [Fact]
    public async Task WebhookInboxService_InvalidSignature_WritesSignatureInvalidRow_DoesNotDispatch()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx, signatureValid: false);
        var req = ValidRequest();

        var result = await h.Service.ReceivePaymentWebhookAsync(req);

        result.IsSuccess.Should().BeFalse();

        var row = await fx.DbContext.WebhookInboxes.AsNoTracking().SingleAsync();
        row.Status.Should().Be(WebhookInboxStatus.SignatureInvalid);
        row.ProviderName.Should().Be("Paystack");

        h.Parser.Verify(x => x.ParsePaymentWebhookAsync(
            It.IsAny<PaymentWebhookRequestDto>(), It.IsAny<CancellationToken>()), Times.Never,
            "signature check short-circuits BEFORE the parser is called");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<Application.Payments.Dtos.ApplyPaymentStatusChangeRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WebhookInboxService_SignatureValidatorThrows_TreatedAsInvalid()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        h.Signature.Setup(x => x.IsValidAsync(It.IsAny<PaymentWebhookRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await h.Service.ReceivePaymentWebhookAsync(ValidRequest());

        result.IsSuccess.Should().BeFalse("thrown validator is treated as invalid — never crashes the caller");
        (await fx.DbContext.WebhookInboxes.CountAsync()).Should().Be(1);
    }

    // ─── Dedup by ProviderEventId ─────────────────────────────────

    [Fact]
    public async Task WebhookInboxService_DuplicateByProviderEventId_ReturnsOriginalWithoutSecondRow()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        ParserReturns(h.Parser, new ParsedPaymentWebhookDto
        {
            EventType = WebhookEventType.Unknown,
        });

        var req = ValidRequest(providerEventId: "evt-DUP");

        var first  = await h.Service.ReceivePaymentWebhookAsync(req);
        var replay = await h.Service.ReceivePaymentWebhookAsync(req);

        first.IsSuccess.Should().BeTrue(first.Message);
        replay.IsSuccess.Should().BeTrue(replay.Message);
        replay.Message.Should().Contain("Duplicate");

        (await fx.DbContext.WebhookInboxes.CountAsync()).Should().Be(1,
            "duplicate must NOT write a second inbox row");
    }

    [Fact]
    public async Task WebhookInboxService_DuplicateByProviderEventId_UsesParsedEventIdWhenRequestOmits()
    {
        // Request omits ProviderEventId but the parser extracts one from
        // the payload. The dedup check must use the parsed id.
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        ParserReturns(h.Parser, new ParsedPaymentWebhookDto
        {
            EventType       = WebhookEventType.Unknown,
            ProviderEventId = "evt-PARSED",
        });

        var req = ValidRequest(providerEventId: null);

        await h.Service.ReceivePaymentWebhookAsync(req);
        var replay = await h.Service.ReceivePaymentWebhookAsync(req);

        replay.Message.Should().Contain("Duplicate");
        (await fx.DbContext.WebhookInboxes.CountAsync()).Should().Be(1);
    }

    // ─── Dedup by IdempotencyKey ──────────────────────────────────

    [Fact]
    public async Task WebhookInboxService_DuplicateByIdempotencyKey_WhenNoEventId_ReturnsOriginal()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        ParserReturns(h.Parser, new ParsedPaymentWebhookDto { EventType = WebhookEventType.Unknown });

        var req = ValidRequest(providerEventId: null, idempotencyKey: "idempo-XYZ");

        await h.Service.ReceivePaymentWebhookAsync(req);
        var replay = await h.Service.ReceivePaymentWebhookAsync(req);

        replay.Message.Should().Contain("Duplicate");
        (await fx.DbContext.WebhookInboxes.CountAsync()).Should().Be(1);
    }

    // ─── Cross-provider isolation ─────────────────────────────────

    [Fact]
    public async Task WebhookInboxService_SameEventIdOnDifferentProviders_IsNotADuplicate()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        ParserReturns(h.Parser, new ParsedPaymentWebhookDto { EventType = WebhookEventType.Unknown });

        var paystackReq = ValidRequest(providerName: "Paystack", providerEventId: "shared-id");
        var payfastReq  = ValidRequest(providerName: "PayFast",  providerEventId: "shared-id");

        await h.Service.ReceivePaymentWebhookAsync(paystackReq);
        await h.Service.ReceivePaymentWebhookAsync(payfastReq);

        (await fx.DbContext.WebhookInboxes.CountAsync()).Should().Be(2,
            "dedup is per-provider; the same providerEventId on a different provider is a distinct event");
    }

    // ─── Parse-failure branch ─────────────────────────────────────

    [Fact]
    public async Task WebhookInboxService_ParseFailure_PersistsFailedRow()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        ParserReturns(h.Parser, parsed: null);

        var result = await h.Service.ReceivePaymentWebhookAsync(ValidRequest());

        result.IsSuccess.Should().BeTrue("parse-failure is stored, but the caller still gets a success + message");

        var row = await fx.DbContext.WebhookInboxes.AsNoTracking().SingleAsync();
        row.Status.Should().Be(WebhookInboxStatus.Failed);
        row.FailureReason.Should().Contain("could not be parsed");
        h.Applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<Application.Payments.Dtos.ApplyPaymentStatusChangeRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}

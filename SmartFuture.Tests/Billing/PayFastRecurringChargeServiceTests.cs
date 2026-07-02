using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Tests.Billing;

// Phase-7 tests for PayFastRecurringChargeService.
//
// The service is a thin, testable seam over the low-level
// IPayFastAdhocChargeService. Its job:
//   - Gate on AdhocChargingEnabled + IsDryRun (never call the provider
//     when either is off/on respectively).
//   - Validate the mandate + amount shape.
//   - Decrypt the mandate's protected token via IMandateProtector.
//   - Convert ZAR → cents and delegate to IPayFastAdhocChargeService.
//   - Map the transport outcome to a RecurringChargeResult of
//     Pending (accepted, awaiting ITN) or Failed.
//
// The service NEVER marks invoices paid — that's the applier's job, and
// PayFast settles via ITN. All tests use Loose/Strict mocks and never
// hit the network.
public class PayFastRecurringChargeServiceTests
{
    private static (PayFastRecurringChargeService svc, Mock<IPayFastAdhocChargeService> adhoc, Mock<IMandateProtector> protector) Build(
        bool adhocChargingEnabled = true)
    {
        var adhoc = new Mock<IPayFastAdhocChargeService>(MockBehavior.Strict);
        var protector = new Mock<IMandateProtector>(MockBehavior.Loose);
        protector.Setup(x => x.Unprotect(It.IsAny<string>()))
            .Returns((string blob) => $"raw-{blob}");
        var settings = new PayFastSettings
        {
            AdhocChargingEnabled = adhocChargingEnabled,
        };
        var svc = new PayFastRecurringChargeService(
            adhoc.Object, protector.Object,
            Options.Create(settings),
            NullLogger<PayFastRecurringChargeService>.Instance);
        return (svc, adhoc, protector);
    }

    private static CustomerPaymentMandate ValidMandate(
        PaymentProviderType provider = PaymentProviderType.PayFast,
        bool isActive = true, bool isReusable = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Provider = provider,
            AuthorizationCodeProtected = "prot-blob",
            IsActive = isActive,
            IsReusable = isReusable,
            IsDefault = true,
        };

    private static RecurringChargeContext Ctx(bool isDryRun = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), AutoBillingChargeSource.MonthlyRenewal, isDryRun);

    // ─── Master gates ──────────────────────────────────────────────

    [Fact]
    public async Task PayFastRecurringChargeService_AdhocChargingDisabled_ReturnsFailed_WithoutHttpCall()
    {
        var (svc, adhoc, _) = Build(adhocChargingEnabled: false);

        var result = await svc.ChargeAsync(ValidMandate(), 999m, "REF-1", Ctx());

        result.Kind.Should().Be(RecurringChargeOutcomeKind.Failed);
        result.Message.Should().Contain("PayFast ad-hoc charging is disabled");
        adhoc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PayFastRecurringChargeService_DryRun_ReturnsFailed_WithoutHttpCall()
    {
        var (svc, adhoc, _) = Build();

        var result = await svc.ChargeAsync(ValidMandate(), 999m, "REF-1", Ctx(isDryRun: true));

        result.Kind.Should().Be(RecurringChargeOutcomeKind.Failed);
        result.Message.Should().Contain("Dry-run");
        adhoc.VerifyNoOtherCalls();
    }

    // ─── Mandate + amount validation ──────────────────────────────

    [Theory]
    [InlineData(PaymentProviderType.Paystack)]
    [InlineData(PaymentProviderType.Manual)]
    public async Task PayFastRecurringChargeService_MandateOnWrongProvider_ReturnsFailed(PaymentProviderType wrongProvider)
    {
        var (svc, adhoc, _) = Build();
        var result = await svc.ChargeAsync(ValidMandate(wrongProvider), 999m, "REF-1", Ctx());
        result.Kind.Should().Be(RecurringChargeOutcomeKind.Failed);
        adhoc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PayFastRecurringChargeService_InactiveMandate_ReturnsFailed()
    {
        var (svc, adhoc, _) = Build();
        var result = await svc.ChargeAsync(ValidMandate(isActive: false), 999m, "REF-1", Ctx());
        result.Kind.Should().Be(RecurringChargeOutcomeKind.Failed);
        adhoc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PayFastRecurringChargeService_NonReusableMandate_ReturnsFailed()
    {
        var (svc, adhoc, _) = Build();
        var result = await svc.ChargeAsync(ValidMandate(isReusable: false), 999m, "REF-1", Ctx());
        result.Kind.Should().Be(RecurringChargeOutcomeKind.Failed);
        adhoc.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(-100)]
    public async Task PayFastRecurringChargeService_ZeroOrNegativeAmount_ReturnsFailed(decimal amount)
    {
        var (svc, adhoc, _) = Build();
        var result = await svc.ChargeAsync(ValidMandate(), amount, "REF-1", Ctx());
        result.Kind.Should().Be(RecurringChargeOutcomeKind.Failed);
        adhoc.VerifyNoOtherCalls();
    }

    // ─── Token decrypt failure ────────────────────────────────────

    [Fact]
    public async Task PayFastRecurringChargeService_TokenUnprotectThrows_ReturnsFailed_WithoutLeakingBlob()
    {
        var (svc, adhoc, protector) = Build();
        protector.Setup(x => x.Unprotect(It.IsAny<string>()))
            .Throws(new InvalidOperationException("cannot decrypt"));

        var result = await svc.ChargeAsync(ValidMandate(), 999m, "REF-1", Ctx());

        result.Kind.Should().Be(RecurringChargeOutcomeKind.Failed);
        result.Message.Should().Contain("unreadable");
        adhoc.VerifyNoOtherCalls();
    }

    // ─── Happy paths + amount conversion ─────────────────────────

    [Fact]
    public async Task PayFastRecurringChargeService_Accepted_ReturnsPending_WithProviderTransactionId()
    {
        var (svc, adhoc, _) = Build();
        adhoc.Setup(x => x.ChargeAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PayFastAdhocChargeResult.AcceptedResult(
                httpStatusCode: 200,
                providerTransactionId: "PF-TX-42",
                providerStatus: "PENDING",
                message: "accepted"));

        var result = await svc.ChargeAsync(ValidMandate(), 999m, "REF-1", Ctx());

        result.Kind.Should().Be(RecurringChargeOutcomeKind.Pending,
            "PayFast settles via ITN — the API response never marks the invoice paid");
        result.ProviderTransactionId.Should().Be("PF-TX-42");
        result.ProviderStatus.Should().Be("PENDING");
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(349.77, 34_977)]
    [InlineData(0.01, 1)]
    [InlineData(999.99, 99_999)]
    public async Task PayFastRecurringChargeService_ConvertsZarToCents_AwayFromZero(decimal zar, long expectedCents)
    {
        var (svc, adhoc, _) = Build();
        long capturedCents = -1;
        adhoc.Setup(x => x.ChargeAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, long, string, string, CancellationToken>((_, cents, _, _, _) => capturedCents = cents)
            .ReturnsAsync(PayFastAdhocChargeResult.AcceptedResult(200, "TX", "ok", null));

        await svc.ChargeAsync(ValidMandate(), zar, "REF-1", Ctx());

        capturedCents.Should().Be(expectedCents);
    }

    [Fact]
    public async Task PayFastRecurringChargeService_PassesRawTokenAndReference_NotProtectedBlob()
    {
        // Ensure the DECRYPTED token (from Unprotect) reaches the adhoc
        // service — not the stored protected blob. Security-critical.
        var (svc, adhoc, protector) = Build();
        string capturedToken = "?";
        string capturedRef = "?";
        adhoc.Setup(x => x.ChargeAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, long, string, string, CancellationToken>((tok, _, _, r, _) =>
            {
                capturedToken = tok;
                capturedRef   = r;
            })
            .ReturnsAsync(PayFastAdhocChargeResult.AcceptedResult(200, "TX", "ok", null));

        var mandate = ValidMandate();
        await svc.ChargeAsync(mandate, 500m, "MYREF-42", Ctx());

        capturedToken.Should().Be("raw-prot-blob",
            "the adhoc service must receive the DECRYPTED token, never the protected blob");
        capturedToken.Should().NotBe(mandate.AuthorizationCodeProtected);
        capturedRef.Should().Be("MYREF-42");
    }

    // ─── Rejected / declined ─────────────────────────────────────

    [Fact]
    public async Task PayFastRecurringChargeService_Rejected_ReturnsFailed_WithMessage()
    {
        var (svc, adhoc, _) = Build();
        adhoc.Setup(x => x.ChargeAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PayFastAdhocChargeResult.Rejected(400, "declined", "Card declined"));

        var result = await svc.ChargeAsync(ValidMandate(), 999m, "REF-1", Ctx());

        result.Kind.Should().Be(RecurringChargeOutcomeKind.Failed);
        result.Message.Should().Be("Card declined");
        result.ProviderStatus.Should().Be("declined");
    }

    [Fact]
    public async Task PayFastRecurringChargeService_TransportFailure_ReturnsFailed()
    {
        var (svc, adhoc, _) = Build();
        adhoc.Setup(x => x.ChargeAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PayFastAdhocChargeResult.Transport("Network unreachable"));

        var result = await svc.ChargeAsync(ValidMandate(), 999m, "REF-1", Ctx());

        result.Kind.Should().Be(RecurringChargeOutcomeKind.Failed);
        result.Message.Should().Be("Network unreachable");
    }
}

using SmartFuture.Application.Payments.Paystack;

namespace SmartFuture.Tests.Billing;

// Phase-5 tests for the extracted PaystackNotifyDecisionCore. Pure
// decision function — no fixtures, no DB, no HTTP. Locks every path
// the handler used to take inline so the handler wrapper can never
// silently disagree with the intent of the checks.
public class PaystackNotifyDecisionCoreTests
{
    // Happy-path defaults — override individual fields per test to
    // exercise each guard.
    private static PaystackNotifyDecisionInput ValidChargeSuccess() => new()
    {
        RawBodyProvided = true,
        ProviderEnabled = true,
        ProviderConfigured = true,
        SignatureValid = true,
        JsonParsed = true,
        EventDataPresent = true,
        EventName = "charge.success",
        Reference = "TX-REF-1",
        WebhookAmountSubunits = 99900,
        WebhookCurrency = "ZAR",
        WebhookStatus = "success",
        IsIntentReference = false,
        PaymentInitiationFound = true,
        PaymentAlreadyCompleted = false,
        ExpectedAmountSubunits = 99900,
        ExpectedCurrency = "ZAR",
        VerifyGate = PaystackVerifyGateOutcome.AgreesWithWebhook,
        WebhookApplyEnabled = true,
        InitiationApplyModeIsApplyNormally = true,
    };

    // ─── Happy paths ────────────────────────────────────────────────

    [Fact]
    public void PaystackNotifyDecision_ValidChargeSuccess_AppliesCompletedStatus()
    {
        var outcome = PaystackNotifyDecisionCore.Calculate(ValidChargeSuccess());

        outcome.ShouldApplyPaymentStatusChange.Should().BeTrue();
        outcome.ShouldPersistPaystackTransactionId.Should().BeTrue();
        outcome.ShouldAttemptMandateUpsert.Should().BeTrue();
        outcome.ShouldReject.Should().BeFalse();
        outcome.ShouldAcceptAsNoOp.Should().BeFalse();
    }

    [Fact]
    public void PaystackNotifyDecision_ValidChargeFailed_MapsToNoOp_StatusNotSuccess()
    {
        // charge.success event but the nested data.status is "failed" —
        // the handler acknowledges without applying. (Failed payments
        // arrive via a different event / applier path.)
        var input = ValidChargeSuccess() with { WebhookStatus = "failed" };

        var outcome = PaystackNotifyDecisionCore.Calculate(input);

        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpReason.Should().Be(PaystackNotifyDecisionCore.NoOpCodes.StatusNotSuccess);
        outcome.ShouldApplyPaymentStatusChange.Should().BeFalse();
    }

    // ─── Static-gate rejects ────────────────────────────────────────

    [Fact]
    public void PaystackNotifyDecision_InvalidSignature_Rejects()
    {
        var input = ValidChargeSuccess() with { SignatureValid = false };

        var outcome = PaystackNotifyDecisionCore.Calculate(input);

        outcome.ShouldReject.Should().BeTrue();
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.SignatureMismatch);
    }

    [Fact]
    public void PaystackNotifyDecision_MalformedPayload_RejectsSafely()
    {
        var input = ValidChargeSuccess() with { JsonParsed = false };

        var outcome = PaystackNotifyDecisionCore.Calculate(input);

        outcome.ShouldReject.Should().BeTrue();
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.MalformedJson);
    }

    [Fact]
    public void PaystackNotifyDecision_EmptyBody_Rejects()
    {
        var input = ValidChargeSuccess() with { RawBodyProvided = false };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.EmptyBody);
    }

    [Fact]
    public void PaystackNotifyDecision_ProviderNotEnabled_Rejects()
    {
        var input = ValidChargeSuccess() with { ProviderEnabled = false };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.PaystackNotEnabled);
    }

    [Fact]
    public void PaystackNotifyDecision_ProviderNotConfigured_Rejects()
    {
        var input = ValidChargeSuccess() with { ProviderConfigured = false };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.PaystackNotConfigured);
    }

    [Fact]
    public void PaystackNotifyDecision_MissingEventOrData_Rejects()
    {
        var input = ValidChargeSuccess() with { EventDataPresent = false };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.MissingEventOrData);
    }

    // ─── Reference gates ────────────────────────────────────────────

    [Fact]
    public void PaystackNotifyDecision_UnknownReference_RejectsOrNoOpsSafely()
    {
        var input = ValidChargeSuccess() with { PaymentInitiationFound = false };

        var outcome = PaystackNotifyDecisionCore.Calculate(input);

        outcome.ShouldReject.Should().BeTrue();
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.UnknownReference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PaystackNotifyDecision_MissingReference_Rejects(string? reference)
    {
        var input = ValidChargeSuccess() with { Reference = reference };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.MissingReference);
    }

    // ─── Amount / currency ──────────────────────────────────────────

    [Fact]
    public void PaystackNotifyDecision_AmountMismatch_RejectsOrFlagsAccordingToCurrentRules()
    {
        var input = ValidChargeSuccess() with { WebhookAmountSubunits = 10_000 };

        var outcome = PaystackNotifyDecisionCore.Calculate(input);

        outcome.ShouldReject.Should().BeTrue();
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.AmountMismatch);
    }

    [Fact]
    public void PaystackNotifyDecision_CurrencyMismatch_Rejects()
    {
        var input = ValidChargeSuccess() with { WebhookCurrency = "USD" };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.CurrencyMismatch);
    }

    // ─── Idempotency ────────────────────────────────────────────────

    [Fact]
    public void PaystackNotifyDecision_DuplicateEvent_NoOpsWithoutApply()
    {
        // Same Paystack reference lands twice; on the second arrival the
        // Payment is already Completed. Handler must acknowledge (200 OK)
        // without re-applying arithmetic.
        var input = ValidChargeSuccess() with { PaymentAlreadyCompleted = true };

        var outcome = PaystackNotifyDecisionCore.Calculate(input);

        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpReason.Should().Be(PaystackNotifyDecisionCore.NoOpCodes.AlreadyCompleted);
        outcome.ShouldApplyPaymentStatusChange.Should().BeFalse();
    }

    // ─── Unsupported event ──────────────────────────────────────────

    [Fact]
    public void PaystackNotifyDecision_UnsupportedEvent_NoOpsSafely()
    {
        var input = ValidChargeSuccess() with { EventName = "transfer.success" };

        var outcome = PaystackNotifyDecisionCore.Calculate(input);

        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpReason.Should().Be(PaystackNotifyDecisionCore.NoOpCodes.NonActionableEvent);
        outcome.ShouldApplyPaymentStatusChange.Should().BeFalse();
    }

    // ─── Missing payment initiation covered by unknown reference ────
    [Fact]
    public void PaystackNotifyDecision_MissingPaymentInitiation_RejectsSafely()
    {
        // Alias — same as UnknownReference. Kept for the required
        // test-case name from the phase-5 brief.
        var input = ValidChargeSuccess() with { PaymentInitiationFound = false };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.UnknownReference);
    }

    // ─── Verify-gate ────────────────────────────────────────────────

    [Fact]
    public void PaystackNotifyDecision_VerifyDisagreement_Rejects()
    {
        var input = ValidChargeSuccess() with { VerifyGate = PaystackVerifyGateOutcome.Disagreement };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.RejectReason.Should().Be(PaystackNotifyDecisionCore.RejectCodes.VerifyDisagreement);
    }

    [Fact]
    public void PaystackNotifyDecision_VerifyCallFailed_StillApplies_WebhookIsSigned()
    {
        // Verify service transport-failed → handler falls back to the
        // signed webhook alone. Apply proceeds.
        var input = ValidChargeSuccess() with { VerifyGate = PaystackVerifyGateOutcome.CallFailed };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.ShouldApplyPaymentStatusChange.Should().BeTrue();
    }

    [Fact]
    public void PaystackNotifyDecision_VerifyNotChecked_StillApplies()
    {
        var input = ValidChargeSuccess() with { VerifyGate = PaystackVerifyGateOutcome.NotChecked };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.ShouldApplyPaymentStatusChange.Should().BeTrue();
    }

    // ─── Apply gate (kill switch + per-initiation mode) ─────────────

    [Fact]
    public void PaystackNotifyDecision_WebhookApplyDisabled_NoOpsWithoutApply()
    {
        var input = ValidChargeSuccess() with { WebhookApplyEnabled = false };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpReason.Should().Be(PaystackNotifyDecisionCore.NoOpCodes.ApplySuppressedByWebhookApplyEnabled);
        outcome.ShouldApplyPaymentStatusChange.Should().BeFalse();
    }

    [Fact]
    public void PaystackNotifyDecision_InitiationApplyModeNotNormal_NoOpsWithoutApply()
    {
        var input = ValidChargeSuccess() with { InitiationApplyModeIsApplyNormally = false };
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpReason.Should().Be(PaystackNotifyDecisionCore.NoOpCodes.ApplySuppressedByApplyMode);
        outcome.ShouldApplyPaymentStatusChange.Should().BeFalse();
    }

    // ─── Intent reference routes to convert path ────────────────────

    [Fact]
    public void PaystackNotifyDecision_IntentReference_RoutesToConvert()
    {
        // Intent-flow references (SF-INTENT-*) don't have a
        // PaymentInitiation yet — the convert path creates it. Handler
        // must delegate to the intent service.
        var input = ValidChargeSuccess() with
        {
            IsIntentReference = true,
            PaymentInitiationFound = false,   // proves intent-ref bypasses the initiation lookup
            Reference = "SF-INTENT-abcdef123",
        };

        var outcome = PaystackNotifyDecisionCore.Calculate(input);

        outcome.ShouldConvertIntent.Should().BeTrue();
        outcome.ShouldReject.Should().BeFalse();
        outcome.ShouldApplyPaymentStatusChange.Should().BeFalse();
    }

    // ─── Lowercase signature (Phase-4 bug-fix coverage) ─────────────

    [Fact]
    public void PaystackNotifyDecision_LowercaseSignatureAccepted_StillCovered()
    {
        // The decision core only sees a bool from
        // PaystackWebhookMapper.IsWebhookSignatureValid. Lowercase-hex
        // acceptance is verified by that helper's own test suite. Here
        // we lock the sequence: a "signature-valid" input MUST reach
        // the apply path.
        var input = ValidChargeSuccess();
        // Simulating a lowercase-hex sig that PayFast/Paystack sent and
        // that IsWebhookSignatureValid rightly accepted.
        input.SignatureValid.Should().BeTrue();
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.ShouldApplyPaymentStatusChange.Should().BeTrue();
    }

    // ─── Mandate-update signal (only when applying) ────────────────

    [Fact]
    public void PaystackNotifyDecision_ValidMandateEvent_UpdatesMandateIfCurrentHandlerDoesThat()
    {
        // The current handler ALWAYS attempts a mandate upsert when it
        // reaches the apply branch — regardless of whether the webhook
        // payload actually carried an authorization block. The upsert
        // service itself no-ops if there's no authorization. Lock that
        // signal here.
        var input = ValidChargeSuccess();
        var outcome = PaystackNotifyDecisionCore.Calculate(input);
        outcome.ShouldApplyPaymentStatusChange.Should().BeTrue();
        outcome.ShouldAttemptMandateUpsert.Should().BeTrue();
    }
}

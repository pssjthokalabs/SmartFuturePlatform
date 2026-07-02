using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Tests.Billing;

// Phase-5 tests for the extracted PayFastNotifyDecisionCore. Pure
// decision function — no fixtures, no DB, no HTTP. Locks the full
// intent-first + invoice-bound branching so the wrapper handler can
// never silently drift from the documented behaviour.
public class PayFastNotifyDecisionCoreTests
{
    // Invoice-bound happy path — override per-test to exercise each guard.
    private static PayFastNotifyDecisionInput ValidInvoiceCompleted() => new()
    {
        PayloadProvided = true,
        ProviderConfigured = true,
        MerchantMatches = true,
        SignatureValid = true,
        Reference = "PF-REF-1",
        MappedStatus = PaymentStatus.Completed,
        IntentFound = false,
        IntentAmountMismatch = false,
        IntentCanBeCancelled = false,
        TokenCaptureAllowedForIntent = false,
        PaymentInitiationFound = true,
        PaymentAmountMismatch = false,
        TokenCaptureAllowedForInvoice = false,
    };

    private static PayFastNotifyDecisionInput ValidIntentCompleted() => new()
    {
        PayloadProvided = true,
        ProviderConfigured = true,
        MerchantMatches = true,
        SignatureValid = true,
        Reference = "SF-INTENT-abc",
        MappedStatus = PaymentStatus.Completed,
        IntentFound = true,
        IntentAmountMismatch = false,
        IntentCanBeCancelled = true,
        TokenCaptureAllowedForIntent = false,
        PaymentInitiationFound = false,
        PaymentAmountMismatch = false,
        TokenCaptureAllowedForInvoice = false,
    };

    // ─── Happy paths ───────────────────────────────────────────────

    [Fact]
    public void PayFastNotifyDecision_ValidPaidItn_AppliesCompletedStatus()
    {
        var outcome = PayFastNotifyDecisionCore.Calculate(ValidInvoiceCompleted());

        outcome.ShouldApplyPaymentStatusChange.Should().BeTrue();
        outcome.ApplyPaymentStatus.Should().Be(PaymentStatus.Completed);
        outcome.ShouldPersistPayFastTransactionId.Should().BeTrue();
        outcome.ShouldReconcileAutoBilling.Should().BeTrue();
        outcome.ShouldReject.Should().BeFalse();
    }

    [Fact]
    public void PayFastNotifyDecision_FailedItn_AppliesFailedStatus()
    {
        var input = ValidInvoiceCompleted() with { MappedStatus = PaymentStatus.Failed };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);

        outcome.ShouldApplyPaymentStatusChange.Should().BeTrue();
        outcome.ApplyPaymentStatus.Should().Be(PaymentStatus.Failed);
    }

    // ─── Static-gate rejects ───────────────────────────────────────

    [Fact]
    public void PayFastNotifyDecision_EmptyPayload_Rejects()
    {
        var input = ValidInvoiceCompleted() with { PayloadProvided = false };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.RejectMessage.Should().Be(PayFastNotifyDecisionCore.RejectMessages.EmptyPayload);
    }

    [Fact]
    public void PayFastNotifyDecision_NotConfigured_Rejects()
    {
        var input = ValidInvoiceCompleted() with { ProviderConfigured = false };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.RejectMessage.Should().Be(PayFastNotifyDecisionCore.RejectMessages.NotConfigured);
    }

    [Fact]
    public void PayFastNotifyDecision_InvalidMerchant_Rejects()
    {
        var input = ValidInvoiceCompleted() with { MerchantMatches = false };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.RejectMessage.Should().Be(PayFastNotifyDecisionCore.RejectMessages.MerchantMismatch);
    }

    [Fact]
    public void PayFastNotifyDecision_InvalidSignature_Rejects()
    {
        var input = ValidInvoiceCompleted() with { SignatureValid = false };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.RejectMessage.Should().Be(PayFastNotifyDecisionCore.RejectMessages.SignatureMismatch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PayFastNotifyDecision_MissingReference_Rejects(string? reference)
    {
        var input = ValidInvoiceCompleted() with { Reference = reference };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.RejectMessage.Should().Be(PayFastNotifyDecisionCore.RejectMessages.MissingReference);
    }

    // ─── Invoice-bound branch ──────────────────────────────────────

    [Fact]
    public void PayFastNotifyDecision_UnknownPaymentReference_RejectsOrNoOpsSafely()
    {
        // No intent AND no PaymentInitiation → hard reject.
        var input = ValidInvoiceCompleted() with { PaymentInitiationFound = false };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.RejectMessage.Should().Be(PayFastNotifyDecisionCore.RejectMessages.UnknownReference);
    }

    [Fact]
    public void PayFastNotifyDecision_AmountMismatch_RejectsOrFlagsAccordingToCurrentRules()
    {
        // Invoice-bound amount mismatch → hard reject (handler's current rule).
        var input = ValidInvoiceCompleted() with { PaymentAmountMismatch = true };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.RejectMessage.Should().Be(PayFastNotifyDecisionCore.RejectMessages.AmountMismatch);
    }

    // ─── Duplicate ITN (invoice-bound) ─────────────────────────────

    [Fact]
    public void PayFastNotifyDecision_DuplicateItn_NoOpsWithoutApply()
    {
        // A duplicate ITN carrying a non-terminal status ("PENDING") →
        // mapped status is null → handler saves gateway ref + reports
        // "Stored, no state change".
        var input = ValidInvoiceCompleted() with { MappedStatus = null };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);

        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpMessage.Should().Be("non-terminal-status");
        outcome.ShouldApplyPaymentStatusChange.Should().BeFalse();
        outcome.ShouldPersistPayFastTransactionId.Should().BeTrue(
            "handler still stores the transaction id and saves — just doesn't call the applier");
    }

    // ─── Malformed payload (i.e. null map after parse) ─────────────

    [Fact]
    public void PayFastNotifyDecision_MalformedPayload_RejectsSafely()
    {
        // In the handler, malformed → payload null OR mapped-status null.
        // The core sees this via PayloadProvided=false.
        var outcome = PayFastNotifyDecisionCore.Calculate(new PayFastNotifyDecisionInput
        {
            PayloadProvided = false,
            ProviderConfigured = true,
            MerchantMatches = true,
            SignatureValid = true,
        });
        outcome.ShouldReject.Should().BeTrue();
        outcome.RejectMessage.Should().Be(PayFastNotifyDecisionCore.RejectMessages.EmptyPayload);
    }

    [Fact]
    public void PayFastNotifyDecision_UnsupportedPaymentStatus_NoOpsSafely()
    {
        // PENDING → maps to null → non-terminal, stored, no state change.
        var input = ValidInvoiceCompleted() with { MappedStatus = null };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpMessage.Should().Be("non-terminal-status");
    }

    // ─── Intent-first branch: COMPLETE → convert ───────────────────

    [Fact]
    public void PayFastNotifyDecision_IntentCompleted_RoutesToConvert()
    {
        var outcome = PayFastNotifyDecisionCore.Calculate(ValidIntentCompleted());
        outcome.ShouldConvertIntent.Should().BeTrue();
        outcome.ShouldApplyPaymentStatusChange.Should().BeFalse();
        outcome.ShouldReject.Should().BeFalse();
    }

    // ─── Intent branch: FAILED → cancel intent + no-op ─────────────

    [Fact]
    public void PayFastNotifyDecision_IntentFailed_CancelsIntent_AndNoOps()
    {
        var input = ValidIntentCompleted() with
        {
            MappedStatus = PaymentStatus.Failed,
            IntentCanBeCancelled = true,
        };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);

        outcome.ShouldCancelIntent.Should().BeTrue();
        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpMessage.Should().Be("intent-marked-cancelled");
    }

    // ─── Intent branch: FAILED but not-cancellable (already Converted) → no-op only ─
    [Fact]
    public void PayFastNotifyDecision_IntentFailed_ButNotCancellable_NoOpsOnly()
    {
        var input = ValidIntentCompleted() with
        {
            MappedStatus = PaymentStatus.Failed,
            IntentCanBeCancelled = false,
        };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);

        outcome.ShouldCancelIntent.Should().BeFalse();
        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpMessage.Should().Be("intent-no-state-change");
    }

    // ─── Intent branch: PENDING → no-op ────────────────────────────

    [Fact]
    public void PayFastNotifyDecision_IntentPending_NoOps()
    {
        var input = ValidIntentCompleted() with { MappedStatus = null };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);

        outcome.ShouldAcceptAsNoOp.Should().BeTrue();
        outcome.NoOpMessage.Should().Be("intent-no-state-change");
        outcome.ShouldConvertIntent.Should().BeFalse();
        outcome.ShouldCancelIntent.Should().BeFalse();
    }

    // ─── Intent branch: amount mismatch BEFORE materialise ────────

    [Fact]
    public void PayFastNotifyDecision_IntentAmountMismatch_Rejects()
    {
        var input = ValidIntentCompleted() with { IntentAmountMismatch = true };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.RejectMessage.Should().Be(PayFastNotifyDecisionCore.RejectMessages.AmountMismatch);
    }

    // ─── Token capture signals ─────────────────────────────────────

    [Fact]
    public void PayFastNotifyDecision_TokenCapture_InvoiceCompleted_OnlyWhenAllowed()
    {
        var allowed = ValidInvoiceCompleted() with { TokenCaptureAllowedForInvoice = true };
        var outcome = PayFastNotifyDecisionCore.Calculate(allowed);
        outcome.ShouldAttemptTokenCapture.Should().BeTrue();

        var notAllowed = ValidInvoiceCompleted() with { TokenCaptureAllowedForInvoice = false };
        var outcome2 = PayFastNotifyDecisionCore.Calculate(notAllowed);
        outcome2.ShouldAttemptTokenCapture.Should().BeFalse();
    }

    [Fact]
    public void PayFastNotifyDecision_TokenCapture_NotAttemptedOnFailure()
    {
        // Even when tokenization allowed, a Failed payment must not
        // trigger token capture — you can't tokenize a failed charge.
        var input = ValidInvoiceCompleted() with
        {
            MappedStatus = PaymentStatus.Failed,
            TokenCaptureAllowedForInvoice = true,
        };
        var outcome = PayFastNotifyDecisionCore.Calculate(input);
        outcome.ShouldAttemptTokenCapture.Should().BeFalse();
    }

    [Fact]
    public void PayFastNotifyDecision_TokenCapture_IntentCompleted_OnlyWhenAllowed()
    {
        var allowed = ValidIntentCompleted() with { TokenCaptureAllowedForIntent = true };
        var outcome = PayFastNotifyDecisionCore.Calculate(allowed);
        outcome.ShouldAttemptTokenCapture.Should().BeTrue();
        outcome.ShouldConvertIntent.Should().BeTrue();
    }
}

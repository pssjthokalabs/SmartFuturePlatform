namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Pure, side-effect-free decision core for a Paystack webhook. Encodes
/// the full "given the parsed payload + server state + verify-gate
/// result, what should we do?" pipeline of
/// <see cref="PaystackNotifyHandler.HandleAsync"/> as a single
/// <see cref="Calculate"/> call.
///
/// Contract (like the other cores):
///   • No DbContext access, no HTTP, no logger, no
///     <see cref="System.DateTime.UtcNow"/>. Everything the core needs
///     to make a decision lives on <see cref="PaystackNotifyDecisionInput"/>.
///   • The outcome exhaustively describes what the orchestrator (the
///     handler wrapper) should DO next — reject with a code, accept as
///     no-op with a reason, convert an OrderIntent, or apply a Payment
///     status change.
///   • The wire-level parsing (JSON → PaystackEvent), signature
///     verification (already covered by <see cref="PaystackWebhookMapper"/>),
///     and DB lookups happen in the handler wrapper and are handed to
///     the core as booleans / value snapshots.
///
/// Decision priority (mirrors the previous inline sequence):
///   1. Static gates: empty body, provider not enabled/configured,
///      signature invalid, malformed JSON, missing event/data.
///   2. Non-actionable event (anything other than <c>charge.success</c>)
///      → accept as no-op.
///   3. Missing reference → reject.
///   4. Intent reference (SF-INTENT-*) → route to the intent conversion
///      path via <see cref="PaystackNotifyDecisionOutcome.ShouldConvertIntent"/>.
///   5. Unknown reference (no PaymentInitiation found) → reject.
///   6. Payment already Completed → accept as no-op (idempotent).
///   7. Cross-checks: currency mismatch, amount mismatch, status not
///      "success" → reject or accept no-op accordingly.
///   8. Verify-gate disagreement → reject (belt-and-braces).
///   9. Apply gate (kill switch / per-initiation ApplyMode) → accept as
///      no-op if suppressed.
///  10. Otherwise → apply status change + persist Paystack transaction
///      id + attempt mandate upsert.
/// </summary>
public static class PaystackNotifyDecisionCore
{
    /// <summary>Well-known reject codes — same strings the handler writes to <c>PaystackWebhookLog.RejectionReason</c>.</summary>
    public static class RejectCodes
    {
        public const string EmptyBody = "empty-body";
        public const string PaystackNotEnabled = "paystack-not-enabled";
        public const string PaystackNotConfigured = "paystack-not-configured";
        public const string SignatureMismatch = "signature-mismatch";
        public const string MalformedJson = "malformed-json";
        public const string MissingEventOrData = "missing-event-or-data";
        public const string MissingReference = "missing-reference";
        public const string UnknownReference = "unknown-reference";
        public const string CurrencyMismatch = "currency-mismatch";
        public const string AmountMismatch = "amount-mismatch";
        public const string VerifyDisagreement = "verify-disagreement";
    }

    /// <summary>Well-known no-op codes — surfaced in the handler's OutcomeMessage.</summary>
    public static class NoOpCodes
    {
        public const string NonActionableEvent = "non-actionable-event";
        public const string AlreadyCompleted = "already-completed";
        public const string StatusNotSuccess = "status-not-success";
        public const string ApplySuppressedByWebhookApplyEnabled = "apply-suppressed:PaymentProcessing.WebhookApplyEnabled=false";
        public const string ApplySuppressedByApplyMode = "apply-suppressed:PaymentInitiation.WebhookApplyMode";
    }

    public static PaystackNotifyDecisionOutcome Calculate(PaystackNotifyDecisionInput input)
    {
        // ─── 1. Static gates ──────────────────────────────────────
        if (!input.RawBodyProvided)
            return Reject(RejectCodes.EmptyBody);
        if (!input.ProviderEnabled)
            return Reject(RejectCodes.PaystackNotEnabled);
        if (!input.ProviderConfigured)
            return Reject(RejectCodes.PaystackNotConfigured);
        if (!input.SignatureValid)
            return Reject(RejectCodes.SignatureMismatch);
        if (!input.JsonParsed)
            return Reject(RejectCodes.MalformedJson);
        if (!input.EventDataPresent)
            return Reject(RejectCodes.MissingEventOrData);

        // ─── 2. Non-actionable event ─────────────────────────────
        // Only charge.success drives a state change today. Every other
        // event is acknowledged (200 OK) with no mutation.
        if (!string.Equals(input.EventName?.Trim(), "charge.success", StringComparison.OrdinalIgnoreCase))
            return NoOp(NoOpCodes.NonActionableEvent);

        // ─── 3. Missing reference ────────────────────────────────
        if (string.IsNullOrWhiteSpace(input.Reference))
            return Reject(RejectCodes.MissingReference);

        // ─── 4. Intent reference → convert path ──────────────────
        if (input.IsIntentReference)
        {
            return new PaystackNotifyDecisionOutcome
            {
                ShouldConvertIntent = true,
            };
        }

        // ─── 5. Unknown reference ────────────────────────────────
        if (!input.PaymentInitiationFound)
            return Reject(RejectCodes.UnknownReference);

        // ─── 6. Already-completed idempotency guard ──────────────
        if (input.PaymentAlreadyCompleted)
            return NoOp(NoOpCodes.AlreadyCompleted);

        // ─── 7. Cross-checks ─────────────────────────────────────
        var webhookCurrency = (input.WebhookCurrency ?? string.Empty).Trim().ToUpperInvariant();
        var expectedCurrency = (input.ExpectedCurrency ?? "ZAR").Trim().ToUpperInvariant();
        if (!string.Equals(webhookCurrency, expectedCurrency, StringComparison.Ordinal))
            return Reject(RejectCodes.CurrencyMismatch);

        if (input.WebhookAmountSubunits != input.ExpectedAmountSubunits)
            return Reject(RejectCodes.AmountMismatch);

        if (!string.Equals(input.WebhookStatus?.Trim(), "success", StringComparison.OrdinalIgnoreCase))
            return NoOp(NoOpCodes.StatusNotSuccess);

        // ─── 8. Verify-gate ──────────────────────────────────────
        // Verify.disagreement → hard reject.
        // Verify.callFailed → proceed on signed webhook only (handler logs).
        // Verify.agrees or NotChecked → proceed.
        if (input.VerifyGate == PaystackVerifyGateOutcome.Disagreement)
            return Reject(RejectCodes.VerifyDisagreement);

        // ─── 9. Apply gate (kill switch + per-initiation mode) ───
        // NOTE: even when apply is suppressed, the original inline
        // handler STILL persisted the Paystack transaction id + saved +
        // ran the (best-effort) mandate upsert. Those pre-apply side
        // effects therefore run for the apply-suppressed no-op path
        // too; the outcome carries the same signals as the apply path
        // AND flags no-op so the wrapper skips the applier call.
        if (!input.WebhookApplyEnabled)
            return new PaystackNotifyDecisionOutcome
            {
                ShouldAcceptAsNoOp = true,
                NoOpReason = NoOpCodes.ApplySuppressedByWebhookApplyEnabled,
                ShouldPersistPaystackTransactionId = true,
                ShouldAttemptMandateUpsert = true,
            };
        if (!input.InitiationApplyModeIsApplyNormally)
            return new PaystackNotifyDecisionOutcome
            {
                ShouldAcceptAsNoOp = true,
                NoOpReason = NoOpCodes.ApplySuppressedByApplyMode,
                ShouldPersistPaystackTransactionId = true,
                ShouldAttemptMandateUpsert = true,
            };

        // ─── 10. Apply status change ─────────────────────────────
        return new PaystackNotifyDecisionOutcome
        {
            ShouldApplyPaymentStatusChange = true,
            ShouldPersistPaystackTransactionId = true,
            ShouldAttemptMandateUpsert = true,
        };
    }

    private static PaystackNotifyDecisionOutcome Reject(string code) => new()
    {
        ShouldReject = true,
        RejectReason = code,
    };

    private static PaystackNotifyDecisionOutcome NoOp(string code) => new()
    {
        ShouldAcceptAsNoOp = true,
        NoOpReason = code,
    };
}

/// <summary>
/// Snapshot of everything the decision core needs. Values are taken by
/// the handler AFTER parsing the JSON + running the signature check +
/// looking up server state, so the core stays pure.
/// </summary>
public sealed record PaystackNotifyDecisionInput
{
    /// <summary>False when the request body is empty / null.</summary>
    public required bool RawBodyProvided { get; init; }

    /// <summary>From <c>PaystackSettings.Enabled</c>.</summary>
    public required bool ProviderEnabled { get; init; }

    /// <summary>From <c>PaystackSettings.IsConfigured</c> — secret key + verify URL present.</summary>
    public required bool ProviderConfigured { get; init; }

    /// <summary>Result of <see cref="PaystackWebhookMapper.IsWebhookSignatureValid"/>.</summary>
    public required bool SignatureValid { get; init; }

    /// <summary>True when JsonSerializer successfully deserialized the body into PaystackEvent.</summary>
    public required bool JsonParsed { get; init; }

    /// <summary>True when the parsed event carries a non-null Data block.</summary>
    public required bool EventDataPresent { get; init; }

    // ─── Parsed payload snapshot ───────────────────────────────
    public string? EventName { get; init; }
    public string? Reference { get; init; }
    public long WebhookAmountSubunits { get; init; }
    public string? WebhookCurrency { get; init; }
    public string? WebhookStatus { get; init; }

    /// <summary>True when the reference is a SF-INTENT-* pattern (OrderIntent flow).</summary>
    public bool IsIntentReference { get; init; }

    // ─── Server state ─────────────────────────────────────────
    public bool PaymentInitiationFound { get; init; }
    public bool PaymentAlreadyCompleted { get; init; }
    public long ExpectedAmountSubunits { get; init; }
    public string ExpectedCurrency { get; init; } = "ZAR";

    // ─── Verify gate ──────────────────────────────────────────
    public PaystackVerifyGateOutcome VerifyGate { get; init; } = PaystackVerifyGateOutcome.NotChecked;

    // ─── Apply gate ───────────────────────────────────────────
    public bool WebhookApplyEnabled { get; init; }
    public bool InitiationApplyModeIsApplyNormally { get; init; } = true;
}

/// <summary>Verify-service gate outcomes the decision core can see.</summary>
public enum PaystackVerifyGateOutcome
{
    /// <summary>Verify service was not called for this webhook (e.g. call disabled).</summary>
    NotChecked = 0,
    /// <summary>Verify call returned success + amount/currency/status all agree with the webhook.</summary>
    AgreesWithWebhook = 1,
    /// <summary>Verify call returned success but at least one field disagrees with the webhook — hard reject.</summary>
    Disagreement = 2,
    /// <summary>Verify call transport failed — proceed on the signed webhook alone (handler logs the fallback).</summary>
    CallFailed = 3,
}

/// <summary>
/// Complete decision the handler wrapper applies to its Paystack log
/// row + the DB + downstream services.
/// </summary>
public sealed record PaystackNotifyDecisionOutcome
{
    public bool ShouldReject { get; init; }
    public string RejectReason { get; init; } = string.Empty;

    public bool ShouldAcceptAsNoOp { get; init; }
    public string NoOpReason { get; init; } = string.Empty;

    public bool ShouldConvertIntent { get; init; }

    public bool ShouldApplyPaymentStatusChange { get; init; }
    public bool ShouldPersistPaystackTransactionId { get; init; }
    public bool ShouldAttemptMandateUpsert { get; init; }
}

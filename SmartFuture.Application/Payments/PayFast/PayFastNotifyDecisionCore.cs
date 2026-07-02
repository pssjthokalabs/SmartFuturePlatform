using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.PayFast;

/// <summary>
/// Pure, side-effect-free decision core for a PayFast ITN. Encodes the
/// full "given the parsed payload + server state, what should we do?"
/// pipeline of <see cref="PayFastNotifyHandler.HandleAsync"/> as a
/// single <see cref="Calculate"/> call.
///
/// Contract (same as the other cores):
///   • No DbContext, no HTTP, no logger, no <see cref="System.DateTime.UtcNow"/>.
///   • Signature verification (already pure in
///     <see cref="PayFastSignatureCalculator"/>) + status mapping
///     (already pure in <see cref="PayFastItnMapper"/>) run BEFORE the
///     core is called; their results are handed in as booleans.
///   • The outcome describes what the handler wrapper should do next.
///
/// Decision priority (mirrors the previous inline sequence):
///   1. Static gates: empty payload, PayFast not configured, merchant
///      mismatch, signature mismatch, missing m_payment_id.
///   2. Intent-first branch: if the reference matches an OrderIntent,
///      route based on the mapped ITN status:
///        • Completed → convert.
///        • Failed    → cancel the intent + accept as no-op.
///        • Pending / unknown → accept as no-op (stored, no state change).
///      Amount mismatch inside this branch → reject.
///   3. Invoice-bound branch:
///        • No PaymentInitiation → reject as unknown reference.
///        • Amount mismatch → reject.
///        • Non-terminal status → accept as no-op (stored, no state change).
///        • Completed / Failed → apply status change.
/// </summary>
public static class PayFastNotifyDecisionCore
{
    public static class RejectMessages
    {
        public const string EmptyPayload = "Empty payload";
        public const string NotConfigured = "PayFast not configured";
        public const string MerchantMismatch = "Merchant ID mismatch";
        public const string SignatureMismatch = "Signature mismatch";
        public const string MissingReference = "Missing m_payment_id";
        public const string UnknownReference = "Unknown reference";
        public const string AmountMismatch = "Amount mismatch";
    }

    public static PayFastNotifyDecisionOutcome Calculate(PayFastNotifyDecisionInput input)
    {
        // ─── 1. Static gates ─────────────────────────────────────
        if (!input.PayloadProvided)
            return Reject(RejectMessages.EmptyPayload);
        if (!input.ProviderConfigured)
            return Reject(RejectMessages.NotConfigured);
        if (!input.MerchantMatches)
            return Reject(RejectMessages.MerchantMismatch);
        if (!input.SignatureValid)
            return Reject(RejectMessages.SignatureMismatch);
        if (string.IsNullOrWhiteSpace(input.Reference))
            return Reject(RejectMessages.MissingReference);

        // ─── 2. Intent-first branch ──────────────────────────────
        if (input.IntentFound)
        {
            // Amount cross-check happens BEFORE materialising the order.
            // Only applies when the intent has a recorded expected amount.
            if (input.IntentAmountMismatch)
                return Reject(RejectMessages.AmountMismatch);

            if (input.MappedStatus == PaymentStatus.Completed)
            {
                return new PayFastNotifyDecisionOutcome
                {
                    ShouldConvertIntent = true,
                    ShouldAttemptTokenCapture = input.TokenCaptureAllowedForIntent,
                };
            }

            if (input.MappedStatus == PaymentStatus.Failed && input.IntentCanBeCancelled)
            {
                return new PayFastNotifyDecisionOutcome
                {
                    ShouldCancelIntent = true,
                    ShouldAcceptAsNoOp = true,
                    NoOpMessage = "intent-marked-cancelled",
                };
            }

            // Pending / unknown → acknowledge, no state change.
            return new PayFastNotifyDecisionOutcome
            {
                ShouldAcceptAsNoOp = true,
                NoOpMessage = "intent-no-state-change",
            };
        }

        // ─── 3. Invoice-bound branch ─────────────────────────────
        if (!input.PaymentInitiationFound)
            return Reject(RejectMessages.UnknownReference);

        if (input.PaymentAmountMismatch)
            return Reject(RejectMessages.AmountMismatch);

        // Non-terminal status → save gateway ref updates, no apply.
        if (input.MappedStatus is null)
        {
            return new PayFastNotifyDecisionOutcome
            {
                ShouldPersistPayFastTransactionId = true,
                ShouldAcceptAsNoOp = true,
                NoOpMessage = "non-terminal-status",
            };
        }

        // Completed OR Failed → apply status change through the applier.
        return new PayFastNotifyDecisionOutcome
        {
            ShouldPersistPayFastTransactionId = true,
            ShouldApplyPaymentStatusChange = true,
            ApplyPaymentStatus = input.MappedStatus.Value,
            ShouldReconcileAutoBilling = true,
            ShouldAttemptTokenCapture = input.TokenCaptureAllowedForInvoice
                                     && input.MappedStatus.Value == PaymentStatus.Completed,
        };
    }

    private static PayFastNotifyDecisionOutcome Reject(string message) => new()
    {
        ShouldReject = true,
        RejectMessage = message,
    };
}

/// <summary>
/// Snapshot of everything the PayFast decision core needs. Values are
/// captured by the handler wrapper AFTER parsing the payload + running
/// the signature/merchant/mapper checks + resolving server state, so
/// the core stays pure.
/// </summary>
public sealed record PayFastNotifyDecisionInput
{
    /// <summary>False when the ITN payload object is null.</summary>
    public required bool PayloadProvided { get; init; }
    /// <summary>From <c>PayFastSettings.IsConfigured</c>.</summary>
    public required bool ProviderConfigured { get; init; }
    /// <summary>Result of <see cref="PayFastItnMapper.MerchantMatches"/>.</summary>
    public required bool MerchantMatches { get; init; }
    /// <summary>Result of <see cref="PayFastSignatureCalculator.SignaturesMatch"/>.</summary>
    public required bool SignatureValid { get; init; }

    /// <summary>PayFast's <c>m_payment_id</c> — the SmartFuture-side reference.</summary>
    public string? Reference { get; init; }
    /// <summary>Mapped PaymentStatus from <see cref="PayFastItnMapper.MapPaymentStatus"/>.</summary>
    public PaymentStatus? MappedStatus { get; init; }

    // ─── Intent branch state ────────────────────────────────
    /// <summary>True when an OrderIntent row was found for (Provider=PayFast, ref).</summary>
    public bool IntentFound { get; init; }
    /// <summary>
    /// True when the intent has a recorded expected amount and the ITN
    /// amount is more than the tolerance (0.01 ZAR) off. Only meaningful
    /// when <see cref="IntentFound"/> is true.
    /// </summary>
    public bool IntentAmountMismatch { get; init; }
    /// <summary>Intent is in a state where it can be flipped to Cancelled.</summary>
    public bool IntentCanBeCancelled { get; init; }
    /// <summary>Tokenization is enabled AND intent flow allowed AND payload carries a token AND intent has a claimed user.</summary>
    public bool TokenCaptureAllowedForIntent { get; init; }

    // ─── Invoice branch state ───────────────────────────────
    /// <summary>True when the PaymentInitiation + Payment + Invoice all resolved for the reference.</summary>
    public bool PaymentInitiationFound { get; init; }
    /// <summary>True when the ITN amount vs the Payment.Amount is more than the tolerance (0.01 ZAR).</summary>
    public bool PaymentAmountMismatch { get; init; }
    /// <summary>Tokenization is enabled AND invoice flow allowed AND payload carries a token.</summary>
    public bool TokenCaptureAllowedForInvoice { get; init; }
}

/// <summary>
/// Complete decision the PayFast handler wrapper applies to server
/// state + the applier + downstream reconcile.
/// </summary>
public sealed record PayFastNotifyDecisionOutcome
{
    public bool ShouldReject { get; init; }
    public string RejectMessage { get; init; } = string.Empty;

    public bool ShouldAcceptAsNoOp { get; init; }
    public string NoOpMessage { get; init; } = string.Empty;

    public bool ShouldConvertIntent { get; init; }
    public bool ShouldCancelIntent { get; init; }

    public bool ShouldApplyPaymentStatusChange { get; init; }
    public PaymentStatus? ApplyPaymentStatus { get; init; }

    public bool ShouldPersistPayFastTransactionId { get; init; }
    public bool ShouldReconcileAutoBilling { get; init; }
    public bool ShouldAttemptTokenCapture { get; init; }
}

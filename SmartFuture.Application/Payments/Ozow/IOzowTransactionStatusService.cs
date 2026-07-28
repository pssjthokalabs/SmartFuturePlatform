namespace SmartFuture.Application.Payments.Ozow;

/// <summary>
/// Pull-based Ozow status check — the recovery counterpart to the
/// push-based notify webhook.
///
/// WHY THIS EXISTS: Ozow order-intents settle by webhook only. If the
/// notification never arrives (wrong Ozow__NotifyUrl, host unreachable,
/// firewall, a 415 from a Content-Type mismatch), the customer has been
/// charged and no Order exists — and nothing in the system can tell that
/// apart from "the customer abandoned checkout".
///
/// This service asks Ozow directly: "what happened to transaction
/// reference X?" That converts an invisible failure into a recoverable
/// one, without asking the customer to pay again.
///
/// Read-only against Ozow. It moves no money and changes nothing on
/// their side; the caller decides what to do with the answer.
///
/// Implementation:
/// <c>SmartFuture.Infrastructure.Payments.Ozow.OzowTransactionStatusService</c>.
/// </summary>
public interface IOzowTransactionStatusService
{
    /// <summary>
    /// Look a transaction up by the TransactionReference we sent at
    /// initiate (SF-INTENT-… for new orders, SF-{paymentNumber} for
    /// invoices). Never throws — transport failures come back as a
    /// failed result.
    /// </summary>
    Task<OzowTransactionStatusResult> GetByReferenceAsync(
        string transactionReference,
        CancellationToken cancellationToken = default);
}

public class OzowTransactionStatusResult
{
    public bool Success { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>True when Ozow returned a record for this reference.
    /// False + Success=true means "we reached Ozow and it has never seen
    /// this reference" — which is itself a definitive answer.</summary>
    public bool Found { get; set; }

    /// <summary>Ozow's status verbatim: Complete / Cancelled / Error /
    /// Abandoned / PendingInvestigation.</summary>
    public string? Status { get; set; }

    public string? TransactionId { get; set; }
    public decimal? Amount { get; set; }
    public bool? IsTest { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>Raw response snippet for the operator log. Truncated;
    /// contains no SmartFuture secrets (it is Ozow's own reply).</summary>
    public string? RawSnippet { get; set; }

    public static OzowTransactionStatusResult Fail(string reason) =>
        new() { Success = false, FailureReason = reason };
}

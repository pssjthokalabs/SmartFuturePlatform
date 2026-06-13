namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0C — Stage 2 of the recurring billing run. Selects due, schedule-
/// linked recurring invoices and auto-charges them through the existing
/// <c>AutoBillingService.ChargeInvoiceAsync</c> (Paystack only). It adds the
/// guards the charge service does not (dry-run gating, per-run cap,
/// pending-initiation/retry skip, re-check-before-charge) but never marks
/// invoices paid and never duplicates charge logic. No retries, no
/// suspension.
/// </summary>
public interface IDueInvoiceChargeRunner
{
    Task<DueInvoiceChargeResult> ChargeDueInvoicesAsync(
        RecurringBillingRunContext context, CancellationToken cancellationToken = default);
}

/// <summary>Counters + notes for one charge stage run (surfaced in BillingRunLog).</summary>
public sealed class DueInvoiceChargeResult
{
    public int CandidatesSelected { get; set; }

    public int ChargesAttempted { get; set; }
    public int ChargesSucceeded { get; set; }
    public int ChargesFailed { get; set; }

    public int WouldCharge { get; set; }                 // dry-run only

    public int SkippedNotDue { get; set; }               // settled/paid/cancelled/void/zero-balance between select & charge
    public int SkippedNoOptIn { get; set; }              // customer auto-billing opt-out / no profile
    public int SkippedNoMandate { get; set; }            // no active default reusable Paystack mandate
    public int SkippedPendingInitiation { get; set; }    // non-stale Pending PaymentInitiation in flight
    public int SkippedPendingRetry { get; set; }         // future-scheduled Pending PaymentRetryAttempt
    public int SkippedInactiveService { get; set; }      // schedule/network account not Active (defensive re-check)
    public int SkippedChargeAuthDisabled { get; set; }   // real run but ChargeAuthorizationEnabled=false

    public bool Truncated { get; set; }                  // MaxChargesPerRun hit
    public int ErrorCount { get; set; }
    public List<string> Errors { get; } = new();
}

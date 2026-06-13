namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0D — Stage 3 of the recurring billing run. Consumes due, Pending
/// <c>PaymentRetryAttempt</c> rows and re-charges them through the existing
/// <c>AutoBillingService.ChargeInvoiceAsync</c> in REUSE mode (the existing
/// attempt row is reused, not duplicated). Paystack only. No grace, no
/// suspension, no NetworkAccount mutation.
/// </summary>
public interface IRetryRunner
{
    Task<RetryRunResult> RunDueRetriesAsync(
        RecurringBillingRunContext context, CancellationToken cancellationToken = default);
}

/// <summary>Counters + notes for one retry stage run (surfaced in BillingRunLog).</summary>
public sealed class RetryRunResult
{
    public int AttemptsSelected { get; set; }

    public int WouldRetry { get; set; }                  // dry-run only
    public int RetriesAttempted { get; set; }
    public int RetriesSucceeded { get; set; }
    public int RetriesFailed { get; set; }

    public int SkippedPaidOrSettled { get; set; }
    public int SkippedNoMandate { get; set; }
    public int SkippedNoOptIn { get; set; }
    public int SkippedPendingInitiation { get; set; }
    public int SkippedInactiveService { get; set; }
    public int SkippedMaxAttempts { get; set; }
    public int SkippedRetryJobDisabled { get; set; }
    public int SkippedChargeAuthDisabled { get; set; }
    public int SkippedAlreadyProcessed { get; set; }

    public bool Truncated { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Errors { get; } = new();
}

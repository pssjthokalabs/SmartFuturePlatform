namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Outcome of a recurring-billing run. In Phase 0A every counter stays 0
/// (all stages are no-op); the summary still records whether the run
/// actually executed or was skipped because the lock was held.
/// </summary>
public sealed class RecurringBillingRunSummary
{
    public Guid RunId { get; set; }
    public Guid? BillingRunLogId { get; set; }

    /// <summary>True when the run was skipped because another run held the lock.</summary>
    public bool SkippedDueToLock { get; set; }

    public bool DryRun { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    // All no-op in Phase 0A.
    public int InvoicesGenerated { get; set; }
    public int ChargesAttempted { get; set; }
    public int ChargesSucceeded { get; set; }
    public int ChargesFailed { get; set; }
    public int RetriesProcessed { get; set; }
    public int SuspensionCandidates { get; set; }

    public List<string> Errors { get; } = new();
}

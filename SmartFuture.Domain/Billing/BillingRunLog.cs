using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Domain.Billing;

/// <summary>
/// Phase 0A — one row per recurring-billing run. Serves two purposes:
///   1. Admin visibility/audit of when the engine ran and what it did.
///   2. DB-backed concurrency lock — a row in <see cref="BillingRunStatus.Running"/>
///      whose <see cref="StartedAtUtc"/> is within the staleness window
///      blocks a second concurrent run.
///
/// In Phase 0A every stage is a no-op, so the counters always finish at
/// zero; the row exists to prove the harness ran and to anchor the lock.
/// </summary>
public class BillingRunLog : BaseEntity
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    public BillingRunStatus Status { get; set; } = BillingRunStatus.Running;
    public BillingRunTrigger TriggeredBy { get; set; } = BillingRunTrigger.Scheduler;
    public bool DryRun { get; set; }

    // Stage counters — all stay 0 in Phase 0A (stages are no-op).
    public int InvoicesGenerated { get; set; }
    public int ChargesAttempted { get; set; }
    public int ChargesSucceeded { get; set; }
    public int ChargesFailed { get; set; }
    public int RetriesProcessed { get; set; }
    public int SuspensionCandidates { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>Host machine name — operator diagnostics for multi-instance deploys.</summary>
    public string? MachineName { get; set; }

    /// <summary>Per-run owner token for the lock (a Guid string).</summary>
    public string InstanceId { get; set; } = string.Empty;

    public string? SummaryJson { get; set; }
    public string? ErrorText { get; set; }
}

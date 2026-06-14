namespace SmartFuture.Application.Payments.BillingOps.Dtos;

/// <summary>
/// Aggregated Billing Ops overview — live engine flags + the last run +
/// computed work counts. Counters and flags only; no customer-identifying
/// or payment-sensitive data.
/// </summary>
public sealed class BillingOpsDashboardDto
{
    // ─── Live engine flags (from configuration) ─────────────────────
    public bool WorkerEnabled { get; set; }
    public bool DryRun { get; set; }
    public bool RetryJobEnabled { get; set; }
    public bool PayFastRecurringEnabled { get; set; }
    public bool PayFastAdhocEnabled { get; set; }
    public bool ReportingEnabled { get; set; }
    public bool ManualInvoiceEnabled { get; set; }

    // ─── Last run snapshot (null when no run has happened) ──────────
    public Guid? LastRunId { get; set; }
    public string? LastRunStatus { get; set; }
    public string? LastRunTrigger { get; set; }
    public bool? LastRunDryRun { get; set; }
    public DateTime? LastRunStartedAtUtc { get; set; }
    public DateTime? LastRunFinishedAtUtc { get; set; }
    public double? LastRunDurationSeconds { get; set; }
    public int LastRunInvoicesGenerated { get; set; }
    public int LastRunChargesAttempted { get; set; }
    public int LastRunChargesSucceeded { get; set; }
    public int LastRunChargesFailed { get; set; }
    public int LastRunRetriesProcessed { get; set; }
    public int LastRunSuspensionCandidates { get; set; }
    public int LastRunErrorCount { get; set; }

    // ─── Computed work counts (current snapshot) ────────────────────
    public int DueInvoices { get; set; }
    public int DueRetries { get; set; }
    public int PendingSettlements { get; set; }
    public int GraceCandidates { get; set; }
    public int NotificationsSent { get; set; }
    public int NotificationsSkippedOrFailed { get; set; }
    public int ManualActionRequiredCount { get; set; }
}

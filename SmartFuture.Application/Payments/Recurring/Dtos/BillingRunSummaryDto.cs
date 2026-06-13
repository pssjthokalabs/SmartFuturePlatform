namespace SmartFuture.Application.Payments.Recurring.Dtos;

/// <summary>
/// Typed, parsed view of a <c>BillingRunLog.SummaryJson</c> payload. Mirrors
/// the camelCase blocks the orchestrator writes. Parsing is best-effort — a
/// null instance means the raw JSON couldn't be parsed (e.g. an older shape),
/// and callers fall back to the raw string.
///
/// Contains counters only — no customer-identifying or payment-sensitive data.
/// </summary>
public sealed class BillingRunSummaryDto
{
    public string? Phase { get; set; }
    public string? Note { get; set; }
    public bool DryRun { get; set; }

    public GenerationSummary? Generation { get; set; }
    public ChargeSummary? Charge { get; set; }
    public RetrySummary? Retry { get; set; }
    public GraceSummary? Grace { get; set; }

    public sealed class GenerationSummary
    {
        public int SchedulesConsidered { get; set; }
        public int InvoicesGenerated { get; set; }
        public int WouldGenerate { get; set; }
        public int DuplicatesSkipped { get; set; }
        public int InactiveSkipped { get; set; }
        public int NonRecurringSkipped { get; set; }
        public bool Truncated { get; set; }
        public int ErrorCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public sealed class ChargeSummary
    {
        public int CandidatesSelected { get; set; }
        public int ChargesAttempted { get; set; }
        public int ChargesSucceeded { get; set; }
        public int ChargesFailed { get; set; }
        public int WouldCharge { get; set; }
        public int SkippedNotDue { get; set; }
        public int SkippedNoOptIn { get; set; }
        public int SkippedNoMandate { get; set; }
        public int SkippedPendingInitiation { get; set; }
        public int SkippedPendingRetry { get; set; }
        public int SkippedInactiveService { get; set; }
        public int SkippedChargeAuthDisabled { get; set; }
        public bool Truncated { get; set; }
        public int ErrorCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public sealed class RetrySummary
    {
        public int AttemptsSelected { get; set; }
        public int WouldRetry { get; set; }
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
        public List<string> Errors { get; set; } = new();
    }

    public sealed class GraceSummary
    {
        public int CandidatesSelected { get; set; }
        public int SuspensionCandidates { get; set; }
        public int WouldSuspend { get; set; }
        public int Suspended { get; set; }
        public int SkippedPaidOrSettled { get; set; }
        public int SkippedPendingRetry { get; set; }
        public int SkippedPendingInitiation { get; set; }
        public int SkippedRetriesNotExhausted { get; set; }
        public int SkippedInactiveService { get; set; }
        public int SkippedGraceNotExpired { get; set; }
        public bool Truncated { get; set; }
        public int ErrorCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }
}

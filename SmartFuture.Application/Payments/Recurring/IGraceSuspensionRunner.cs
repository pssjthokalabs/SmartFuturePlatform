namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0E — Stage 4 of the recurring billing run. REPORT-ONLY: detects
/// services eligible for suspension (unpaid recurring invoice after retry
/// exhaustion + grace-period expiry) and records them as suspension
/// candidates. It does NOT suspend, does NOT mutate <c>NetworkAccount.Status</c>,
/// does NOT write notes, and does NOT send notifications. Actual suspension
/// is deferred to a future Phase 0E2.
/// </summary>
public interface IGraceSuspensionRunner
{
    Task<GraceSuspensionResult> DetectCandidatesAsync(
        RecurringBillingRunContext context, CancellationToken cancellationToken = default);
}

/// <summary>Counters + notes for one grace stage run (surfaced in BillingRunLog).</summary>
public sealed class GraceSuspensionResult
{
    public int CandidatesSelected { get; set; }

    /// <summary>Confirmed suspension candidates (all checks passed). Report-only.</summary>
    public int SuspensionCandidates { get; set; }

    /// <summary>Forecast: candidates that WOULD suspend if the suspension path were enabled+implemented.</summary>
    public int WouldSuspend { get; set; }

    /// <summary>Always 0 in Phase 0E — no actual suspension occurs.</summary>
    public int Suspended { get; set; }

    public int SkippedPaidOrSettled { get; set; }
    public int SkippedPendingRetry { get; set; }
    public int SkippedPendingInitiation { get; set; }
    public int SkippedRetriesNotExhausted { get; set; }
    public int SkippedInactiveService { get; set; }
    public int SkippedGraceNotExpired { get; set; }

    public bool Truncated { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Errors { get; } = new();
}

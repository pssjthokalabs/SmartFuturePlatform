using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.Recurring.Dtos;

/// <summary>
/// Single recurring-billing run with its parsed stage summary. Non-sensitive
/// fields only.
/// </summary>
public sealed class BillingRunDetailDto
{
    public Guid Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public BillingRunStatus Status { get; set; }
    public BillingRunTrigger TriggeredBy { get; set; }
    public bool DryRun { get; set; }

    public int InvoicesGenerated { get; set; }
    public int ChargesAttempted { get; set; }
    public int ChargesSucceeded { get; set; }
    public int ChargesFailed { get; set; }
    public int RetriesProcessed { get; set; }
    public int SuspensionCandidates { get; set; }
    public int ErrorCount { get; set; }

    public string? MachineName { get; set; }
    public string? ErrorText { get; set; }

    /// <summary>Parsed stage blocks; null when the raw JSON couldn't be parsed.</summary>
    public BillingRunSummaryDto? Summary { get; set; }

    /// <summary>Raw SummaryJson — always present as a fallback.</summary>
    public string? SummaryJsonRaw { get; set; }
}

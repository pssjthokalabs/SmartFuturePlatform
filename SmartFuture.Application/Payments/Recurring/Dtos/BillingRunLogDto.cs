using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.Recurring.Dtos;

/// <summary>
/// Read-only projection of a <c>BillingRunLog</c> row for admin visibility.
/// </summary>
public sealed class BillingRunLogDto
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
    public string? SummaryJson { get; set; }
    public string? ErrorText { get; set; }
}

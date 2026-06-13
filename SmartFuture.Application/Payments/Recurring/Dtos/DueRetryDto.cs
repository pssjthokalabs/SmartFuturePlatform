using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring.Dtos;

/// <summary>
/// Read-only view of a due, Pending <c>PaymentRetryAttempt</c> (observational
/// mirror of the Stage 3 due-retry selection). Non-sensitive fields only.
/// </summary>
public sealed class DueRetryDto
{
    public Guid AttemptId { get; set; }
    public int AttemptNumber { get; set; }
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime ScheduledForUtc { get; set; }
    public decimal Amount { get; set; }
    public PaymentRetryAttemptStatus Status { get; set; }
}

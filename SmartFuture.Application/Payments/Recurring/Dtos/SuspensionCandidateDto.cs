namespace SmartFuture.Application.Payments.Recurring.Dtos;

/// <summary>
/// Read-only suspension candidate — a service eligible for suspension
/// because of an unpaid recurring invoice after retry exhaustion + grace
/// expiry. Observational mirror of the Stage 4 candidate query; computing it
/// performs NO mutation, NO notification, NO forecast side effect.
/// Non-sensitive fields only.
/// </summary>
public sealed class SuspensionCandidateDto
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public Guid ScheduleId { get; set; }
    public Guid NetworkAccountId { get; set; }
    public decimal BalanceDue { get; set; }
    public DateTime DueAtUtc { get; set; }
    public double DaysOverdue { get; set; }
    public int MaxAttemptNumber { get; set; }
    public string Reason { get; set; } = "RetryExhaustedGraceExpired";
}

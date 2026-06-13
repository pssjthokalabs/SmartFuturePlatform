using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.Recurring.Dtos;

/// <summary>
/// Read-only view of a due, schedule-linked recurring invoice (observational
/// mirror of the Stage 2 due-invoice selection). Non-sensitive fields only.
/// </summary>
public sealed class DueInvoiceDto
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public Guid ScheduleId { get; set; }
    public Guid OrderId { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public decimal BalanceDue { get; set; }
    public InvoiceStatus Status { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";
}

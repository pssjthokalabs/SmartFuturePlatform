using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Billing.Dtos;

public class AdminUpdateInvoiceStatusDto
{
    public InvoiceStatus Status { get; set; }
    public string? AdminNotes { get; set; }
}

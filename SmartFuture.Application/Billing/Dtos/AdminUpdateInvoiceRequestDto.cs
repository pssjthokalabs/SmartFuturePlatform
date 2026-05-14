namespace SmartFuture.Application.Billing.Dtos;

public class AdminUpdateInvoiceRequestDto
{
    public DateTime? DueAtUtc { get; set; }
    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }
    public string? ExternalReference { get; set; }
}

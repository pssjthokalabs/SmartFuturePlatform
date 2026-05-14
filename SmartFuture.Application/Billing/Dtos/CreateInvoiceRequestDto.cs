namespace SmartFuture.Application.Billing.Dtos;

public class CreateInvoiceRequestDto
{
    public Guid OrderId { get; set; }
    public decimal SubtotalAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }
    public string? ExternalReference { get; set; }
}

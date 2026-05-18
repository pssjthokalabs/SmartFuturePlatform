using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Domain.Billing;

// One row on an Invoice (service line, installation fee, discount, etc.).
// Persisted server-side so a customer-facing breakdown can be shown
// without recomputing amounts from the Order snapshot every time.
public class InvoiceLineItem : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public InvoiceLineItemType LineType { get; set; } = InvoiceLineItemType.Other;

    public string Description { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;
    public decimal UnitAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public int SortOrder { get; set; }
}

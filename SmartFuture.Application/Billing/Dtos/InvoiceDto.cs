using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Application.Billing.Dtos;

public class InvoiceDto
{
    public Guid Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public OrderStatus? OrderStatus { get; set; }

    public InvoiceStatus Status { get; set; }

    public decimal SubtotalAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal BalanceDue { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";

    public DateTime? IssuedAtUtc { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public DateTime? VoidedAtUtc { get; set; }

    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }
    public string? ExternalReference { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public string? LastStatusChangedByUserEmail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    // Line-item breakdown (service, installation fee, etc.). Populated
    // by the detail endpoints (`GetMineByIdAsync` / `GetAdminByIdAsync`).
    // List endpoints leave this empty to keep the page payload small —
    // the row UI only needs the rolled-up `TotalAmount`.
    public IReadOnlyList<InvoiceLineItemDto> LineItems { get; set; } = Array.Empty<InvoiceLineItemDto>();

    // Phase 48 — optional service-link metadata. Populated by the
    // billing overview endpoint (and any other call site that has the
    // NetworkAccount loaded). Leaving these null is fine; the UI falls
    // back to showing the order reference only.
    public Guid? ServiceId { get; set; }
    public string? ServiceAccountNumber { get; set; }
    public string? ServicePackageName { get; set; }

    // Phase 49 — customer snapshot pulled from the linked Order. The
    // Order captures full contact details at submission time, so this
    // never goes stale even if the User record is later edited. Admin
    // billing list needs these to render the Customer column at all.
    public Guid? CustomerUserId { get; set; }
    public string? CustomerFullName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhoneNumber { get; set; }
}

public class InvoiceLineItemDto
{
    public Guid Id { get; set; }
    public InvoiceLineItemType LineType { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public int SortOrder { get; set; }
}

using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Domain.Billing;

public class Invoice : BaseEntity
{
    public string InvoiceNumber { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public Order? Order { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

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
    public User? LastStatusChangedByUser { get; set; }

    // ─── Phase 0A — recurring-billing period linkage (additive, nullable) ──
    //
    // Set by the (future, Phase 0B) recurring invoice generator so a
    // recurring invoice is unambiguously tied to one service schedule and
    // one billing period. NULL for all existing/manually-raised invoices.
    // Nothing in Phase 0A writes these.
    public Guid? ServiceBillingScheduleId { get; set; }
    public ServiceBillingSchedule? ServiceBillingSchedule { get; set; }
    public DateTime? PeriodStartUtc { get; set; }
    public DateTime? PeriodEndUtc { get; set; }

    public ICollection<InvoiceLineItem> LineItems { get; set; } = new List<InvoiceLineItem>();
}

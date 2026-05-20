using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Billing.Dtos;

public class InvoiceFilterRequestDto : PagedListQueryBase
{
    public Guid? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public InvoiceStatus? StatusFilter { get; set; }

    public DateTime? IssuedFromUtc { get; set; }
    public DateTime? IssuedToUtc { get; set; }
    public DateTime? DueFromUtc { get; set; }
    public DateTime? DueToUtc { get; set; }
    public DateTime? PaidFromUtc { get; set; }
    public DateTime? PaidToUtc { get; set; }

    // Phase 49 — finance UX filters. Customer/service id let the page
    // pre-scope when the admin clicked through from a profile, and
    // min/max amount supports "show me invoices over R 5 000".
    public Guid? CustomerUserId { get; set; }
    public Guid? ServiceId { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
}

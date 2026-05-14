using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Billing.Dtos;

public class InvoiceFilterRequestDto : PagedListQueryBase
{
    public Guid? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public new InvoiceStatus? Status { get; set; }

    public DateTime? IssuedFromUtc { get; set; }
    public DateTime? IssuedToUtc { get; set; }
    public DateTime? DueFromUtc { get; set; }
    public DateTime? DueToUtc { get; set; }
    public DateTime? PaidFromUtc { get; set; }
    public DateTime? PaidToUtc { get; set; }
}

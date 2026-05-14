using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Billing.Dtos;

public class DebitOrderMandateFilterRequestDto : PagedListQueryBase
{
    public Guid? UserId { get; set; }
    public Guid? CustomerProfileId { get; set; }
    public Guid? OrderId { get; set; }
    public new DebitOrderMandateStatus? Status { get; set; }
    public DebitOrderFrequency? Frequency { get; set; }
    public int? PreferredDebitDay { get; set; }

    public DateTime? StartFromUtc { get; set; }
    public DateTime? StartToUtc { get; set; }
}

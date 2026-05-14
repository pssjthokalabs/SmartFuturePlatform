using SmartFuture.Shared.Enums.Privacy;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Privacy.Dtos;

public class PrivacyRequestFilterRequestDto : PagedListQueryBase
{
    public Guid? UserId { get; set; }
    public PrivacyRequestType? Type { get; set; }
    public new PrivacyRequestStatus? Status { get; set; }

    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtc { get; set; }
    public DateTime? CompletedFromUtc { get; set; }
    public DateTime? CompletedToUtc { get; set; }
}

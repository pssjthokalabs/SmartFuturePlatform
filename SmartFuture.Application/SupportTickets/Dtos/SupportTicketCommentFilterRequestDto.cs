using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.SupportTickets.Dtos;

public class SupportTicketCommentFilterRequestDto : PagedListQueryBase
{
    public Guid SupportTicketId { get; set; }
    public bool IncludeInternal { get; set; }
}

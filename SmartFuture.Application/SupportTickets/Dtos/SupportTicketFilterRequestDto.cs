using SmartFuture.Shared.Enums.SupportTickets;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.SupportTickets.Dtos;

public class SupportTicketFilterRequestDto : PagedListQueryBase
{
    public Guid? UserId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? CoverageRequestId { get; set; }
    public Guid? InstallationId { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? DebitOrderMandateId { get; set; }

    public SupportTicketStatus? StatusFilter { get; set; }
    public SupportTicketCategory? Category { get; set; }
    public SupportTicketPriority? Priority { get; set; }
    public SupportTicketSource? Source { get; set; }
    public Guid? AssignedToUserId { get; set; }

    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtc { get; set; }
    public DateTime? ResolvedFromUtc { get; set; }
    public DateTime? ResolvedToUtc { get; set; }
}

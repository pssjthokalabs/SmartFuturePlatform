using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;

namespace SmartFuture.Domain.SupportTickets;

public class SupportTicketComment : BaseEntity
{
    public Guid SupportTicketId { get; set; }
    public SupportTicket? SupportTicket { get; set; }

    public Guid AuthorUserId { get; set; }
    public User? AuthorUser { get; set; }

    public string Body { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public bool IsSystemGenerated { get; set; }
}

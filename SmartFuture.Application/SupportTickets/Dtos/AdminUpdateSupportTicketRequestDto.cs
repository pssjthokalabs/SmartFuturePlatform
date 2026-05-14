using SmartFuture.Shared.Enums.SupportTickets;

namespace SmartFuture.Application.SupportTickets.Dtos;

public class AdminUpdateSupportTicketRequestDto
{
    public SupportTicketCategory Category { get; set; } = SupportTicketCategory.General;
    public SupportTicketPriority Priority { get; set; } = SupportTicketPriority.Normal;
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? InternalSummary { get; set; }
    public string? ResolutionSummary { get; set; }
}

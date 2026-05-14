using SmartFuture.Shared.Enums.SupportTickets;

namespace SmartFuture.Application.SupportTickets.Dtos;

public class AdminUpdateSupportTicketStatusDto
{
    public SupportTicketStatus Status { get; set; }
    public string? ResolutionSummary { get; set; }
    public string? InternalComment { get; set; }
}

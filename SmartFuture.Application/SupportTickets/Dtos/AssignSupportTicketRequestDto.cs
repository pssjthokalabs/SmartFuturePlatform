namespace SmartFuture.Application.SupportTickets.Dtos;

public class AssignSupportTicketRequestDto
{
    public Guid? AssignedToUserId { get; set; }
    public string? InternalComment { get; set; }
}

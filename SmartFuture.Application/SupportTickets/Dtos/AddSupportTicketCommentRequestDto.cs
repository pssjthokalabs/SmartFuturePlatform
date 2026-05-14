namespace SmartFuture.Application.SupportTickets.Dtos;

public class AddSupportTicketCommentRequestDto
{
    public string Body { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
}

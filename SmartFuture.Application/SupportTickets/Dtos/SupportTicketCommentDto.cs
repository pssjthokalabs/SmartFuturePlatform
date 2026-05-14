namespace SmartFuture.Application.SupportTickets.Dtos;

public class SupportTicketCommentDto
{
    public Guid Id { get; set; }
    public Guid SupportTicketId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string? AuthorUserEmail { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public bool IsSystemGenerated { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

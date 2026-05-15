namespace SmartFuture.Application.Dashboard.Dtos;

public class AdminSupportTicketSummaryDto
{
    public Guid Id { get; set; }
    public string TicketNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

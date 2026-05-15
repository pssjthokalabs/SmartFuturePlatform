namespace SmartFuture.Application.Dashboard.Dtos;

public class AdminRecentAuditLogDto
{
    public Guid Id { get; set; }
    public string? AdminUser { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? EntityName { get; set; }
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; }
}

namespace SmartFuture.Application.Dashboard.Dtos;

public class AdminRecentOrderDto
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

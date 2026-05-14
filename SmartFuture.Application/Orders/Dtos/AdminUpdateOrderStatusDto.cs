using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Application.Orders.Dtos;

public class AdminUpdateOrderStatusDto
{
    public OrderStatus Status { get; set; }
    public string? AdminNotes { get; set; }
    public string? CancellationReason { get; set; }
    public string? FailureReason { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime? ExpectedInstallationDateUtc { get; set; }
}

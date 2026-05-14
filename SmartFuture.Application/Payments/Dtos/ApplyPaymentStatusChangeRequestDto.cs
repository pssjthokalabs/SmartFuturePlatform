using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.Dtos;

public class ApplyPaymentStatusChangeRequestDto
{
    public Guid PaymentId { get; set; }
    public PaymentStatus NewStatus { get; set; }
    public string? FailureReason { get; set; }
    public string? GatewayTransactionId { get; set; }
    public string? GatewayReference { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public bool TriggerNotifications { get; set; }
}

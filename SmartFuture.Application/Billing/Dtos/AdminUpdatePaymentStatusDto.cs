using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Billing.Dtos;

public class AdminUpdatePaymentStatusDto
{
    public PaymentStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public string? AdminNotes { get; set; }
}

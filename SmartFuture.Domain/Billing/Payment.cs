using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Domain.Billing;

public class Payment : BaseEntity
{
    public string PaymentNumber { get; set; } = string.Empty;

    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public PaymentMethodType Method { get; set; } = PaymentMethodType.Other;

    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";

    public DateTime? PaidAtUtc { get; set; }
    public DateTime? FailedAtUtc { get; set; }
    public DateTime? RefundedAtUtc { get; set; }

    public string? GatewayName { get; set; }
    public string? GatewayReference { get; set; }
    public string? GatewayTransactionId { get; set; }
    public string? ExternalReference { get; set; }
    public string? FailureReason { get; set; }
    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public User? LastStatusChangedByUser { get; set; }
}

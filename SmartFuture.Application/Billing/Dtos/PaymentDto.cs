using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Billing.Dtos;

public class PaymentDto
{
    public Guid Id { get; set; }
    public string PaymentNumber { get; set; } = string.Empty;

    public Guid InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public Guid? OrderId { get; set; }
    public string? OrderNumber { get; set; }

    public PaymentStatus Status { get; set; }
    public PaymentMethodType Method { get; set; }

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
    public string? LastStatusChangedByUserEmail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    // Phase 48 — optional service-link metadata, mirroring InvoiceDto.
    // Populated when the call site has the NetworkAccount in scope
    // (notably the billing overview endpoint).
    public Guid? ServiceId { get; set; }
    public string? ServiceAccountNumber { get; set; }
    public string? ServicePackageName { get; set; }
}

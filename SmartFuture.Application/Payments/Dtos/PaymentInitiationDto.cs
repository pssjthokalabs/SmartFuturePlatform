using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Dtos;

public class PaymentInitiationDto
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public Guid? PaymentId { get; set; }
    public string? PaymentNumber { get; set; }

    public PaymentProviderType Provider { get; set; }
    public PaymentInitiationStatus Status { get; set; }

    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";

    public string? ProviderReference { get; set; }
    public string? ProviderCheckoutId { get; set; }
    public string? RedirectUrl { get; set; }
    public string? SuccessUrl { get; set; }
    public string? CancelUrl { get; set; }
    public string? FailureUrl { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public string? MetadataJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

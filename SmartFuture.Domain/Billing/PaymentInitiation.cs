using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Domain.Billing;

public class PaymentInitiation : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public Guid? PaymentId { get; set; }
    public Payment? Payment { get; set; }

    public PaymentProviderType Provider { get; set; } = PaymentProviderType.Manual;
    public PaymentInitiationStatus Status { get; set; } = PaymentInitiationStatus.Created;

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
}

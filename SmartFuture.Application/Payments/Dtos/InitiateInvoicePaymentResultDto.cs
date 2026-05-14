using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Dtos;

public class InitiateInvoicePaymentResultDto
{
    public bool Success { get; set; }
    public PaymentProviderType Provider { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? PaymentInitiationId { get; set; }
    public string? PaymentNumber { get; set; }
    public string? ProviderReference { get; set; }
    public string? RedirectUrl { get; set; }
    public string? FailureReason { get; set; }
}

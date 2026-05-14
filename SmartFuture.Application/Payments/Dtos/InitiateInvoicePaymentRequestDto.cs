using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Dtos;

public class InitiateInvoicePaymentRequestDto
{
    public Guid InvoiceId { get; set; }
    public PaymentProviderType Provider { get; set; } = PaymentProviderType.Manual;
    public decimal? Amount { get; set; }

    public string? SuccessUrl { get; set; }
    public string? CancelUrl { get; set; }
    public string? FailureUrl { get; set; }
}

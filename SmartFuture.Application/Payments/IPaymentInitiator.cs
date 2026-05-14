using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments;

public interface IPaymentInitiator
{
    PaymentProviderType Provider { get; }

    Task<PaymentProviderInitiationResult> InitiateAsync(
        Invoice invoice,
        Payment payment,
        InitiateInvoicePaymentRequestDto request,
        CancellationToken cancellationToken = default);
}

using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments;

public interface IPaymentProviderRegistry
{
    IPaymentInitiator? GetInitiator(PaymentProviderType provider);
}

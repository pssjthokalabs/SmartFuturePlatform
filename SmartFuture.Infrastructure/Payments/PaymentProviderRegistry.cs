using SmartFuture.Application.Payments;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Infrastructure.Payments;

public class PaymentProviderRegistry : IPaymentProviderRegistry
{
    private readonly Dictionary<PaymentProviderType, IPaymentInitiator> _initiators;

    public PaymentProviderRegistry(IEnumerable<IPaymentInitiator> initiators)
    {
        _initiators = initiators.ToDictionary(i => i.Provider);
    }

    public IPaymentInitiator? GetInitiator(PaymentProviderType provider)
        => _initiators.TryGetValue(provider, out var initiator) ? initiator : null;
}

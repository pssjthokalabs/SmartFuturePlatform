using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Default resolver — indexes the registered <see cref="IRecurringChargeService"/>
/// implementations by their <see cref="IRecurringChargeService.Provider"/>.
/// DI injects every registered implementation; Phase 0A registers only
/// the Paystack one.
/// </summary>
public sealed class RecurringChargeServiceResolver : IRecurringChargeServiceResolver
{
    private readonly IReadOnlyDictionary<PaymentProviderType, IRecurringChargeService> _byProvider;

    public RecurringChargeServiceResolver(IEnumerable<IRecurringChargeService> services)
    {
        // Last-registration-wins if two share a provider (none do today).
        var map = new Dictionary<PaymentProviderType, IRecurringChargeService>();
        foreach (var svc in services)
            map[svc.Provider] = svc;
        _byProvider = map;
    }

    public bool IsSupported(PaymentProviderType provider) => _byProvider.ContainsKey(provider);

    public IRecurringChargeService Resolve(PaymentProviderType provider)
    {
        if (_byProvider.TryGetValue(provider, out var svc))
            return svc;

        throw new NotSupportedException(
            $"No recurring-charge service is registered for provider '{provider}'. " +
            "Phase 0A registers Paystack only; PayFast recurring is deferred.");
    }
}

using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Resolves the <see cref="IRecurringChargeService"/> for a given
/// payment provider. Phase 0A registers Paystack only; resolving an
/// unregistered provider throws <see cref="NotSupportedException"/> so a
/// misconfiguration fails loudly rather than silently skipping a charge.
/// </summary>
public interface IRecurringChargeServiceResolver
{
    IRecurringChargeService Resolve(PaymentProviderType provider);
    bool IsSupported(PaymentProviderType provider);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 1C implementation. Selects the customer's default reusable mandate
/// among supported providers, applying the PayFast gate and the
/// Paystack-first deterministic priority.
/// </summary>
public sealed class RecurringMandateSelector : IRecurringMandateSelector
{
    private readonly IAppDbContext _dbContext;
    private readonly AutoBillingSettings _settings;

    public RecurringMandateSelector(IAppDbContext dbContext, IOptions<AutoBillingSettings> settings)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
    }

    public async Task<CustomerPaymentMandate?> ResolveDefaultChargeableMandateAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var candidates = await LoadDefaultMandatesAsync(userId, cancellationToken);

        return candidates
            .Where(IsEnabled)
            // Paystack first (proven + synchronous), then most-recent default.
            .OrderByDescending(m => m.Provider == PaymentProviderType.Paystack)
            .ThenByDescending(m => m.UpdatedAtUtc ?? m.CreatedAtUtc)
            .FirstOrDefault();
    }

    public async Task<MandateAvailability> GetAvailabilityAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var candidates = await LoadDefaultMandatesAsync(userId, cancellationToken);

        if (candidates.Any(IsEnabled))
            return MandateAvailability.Available;

        // No enabled mandate. Distinguish "PayFast exists but disabled" so
        // operators see WHY (vs. genuinely no mandate on file).
        if (candidates.Any(m => m.Provider == PaymentProviderType.PayFast))
            return MandateAvailability.PayFastRecurringDisabled;

        return MandateAvailability.NoReusableMandate;
    }

    /// <summary>Active, reusable, default mandates for supported providers (Paystack, PayFast).</summary>
    private Task<List<CustomerPaymentMandate>> LoadDefaultMandatesAsync(Guid userId, CancellationToken cancellationToken)
        => _dbContext.CustomerPaymentMandates
            .AsNoTracking()
            .Where(m => m.UserId == userId
                     && m.IsActive
                     && m.IsReusable
                     && m.IsDefault
                     && (m.Provider == PaymentProviderType.Paystack || m.Provider == PaymentProviderType.PayFast))
            .ToListAsync(cancellationToken);

    private bool IsEnabled(CustomerPaymentMandate m) =>
        m.Provider == PaymentProviderType.Paystack
        || (m.Provider == PaymentProviderType.PayFast && _settings.EnablePayFastRecurring);
}

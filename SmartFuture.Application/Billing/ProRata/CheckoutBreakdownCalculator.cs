using SmartFuture.Application.Billing;
using SmartFuture.Domain.ServicePackages;

namespace SmartFuture.Application.Billing.ProRata;

/// <summary>
/// Stateless helper that computes the "due today" breakdown for a
/// checkout — activation fee + pro-rata + total. Extracted from
/// <c>OrderIntentService.ComputeCheckoutBreakdown</c> so the money-
/// shaping rule is unit-testable without loading the full
/// <c>OrderIntentService</c> DI graph.
///
/// Business rule (mirrors the private method it replaced):
///   • Activation fee = variant override (respecting free flag) →
///     package fee → R100 launch floor for non-free packages.
///   • Pro-rata only charged at CHECKOUT for product lines whose
///     <c>BillingSettings.ChargeProRataAtCheckout</c> returns true
///     (Security today). Fibre pays activation only at checkout; its
///     pro-rata is issued at admin activation via
///     <see cref="FibreActivationProRataFactory"/>.
///   • Pro-rata is <c>ProRataCalculator.Quote(monthly, now, billingDay)</c>
///     where monthly = variant.Price ?? package.Price.
///
/// The R100 launch floor is the pre-release hotfix from
/// <c>OrderIntentService.Phase53.cs</c>. It applies only to non-free
/// packages: a genuinely free-activation package pays 0.
/// </summary>
public static class CheckoutBreakdownCalculator
{
    // Pre-release launch floor for the activation fee. Non-free packages
    // never charge less than this even when no positive fee is configured.
    // Kept in step with OrderIntentService.MinimumInstallationFee — moved
    // here so BOTH production and tests share the same constant.
    public const decimal MinimumInstallationFee = 100m;

    public sealed record CheckoutBreakdown(
        decimal ActivationFee,
        decimal ProRataAmount,
        int ProRataDays,
        DateTime? ProRataPeriodStartUtc,
        DateTime? ProRataPeriodEndUtc)
    {
        public decimal TotalDueNow => ActivationFee + ProRataAmount;
    }

    /// <summary>
    /// Activation fee with the R100 launch floor for non-free packages.
    /// Free-activation packages (either the package flag or the variant
    /// override) charge 0.
    /// </summary>
    public static decimal ResolveActivationFee(ServicePackage pkg, ServicePackageVariant? variant = null)
    {
        var free = variant is null ? pkg.HasFreeInstallation : (variant.HasFreeInstallation ?? pkg.HasFreeInstallation);
        if (free) return 0m;
        var configured = (variant is null ? pkg.InstallationFee : (variant.InstallationFee ?? pkg.InstallationFee)) ?? 0m;
        return configured > 0m ? configured : MinimumInstallationFee;
    }

    /// <summary>
    /// Effective monthly price — variant overrides package.
    /// </summary>
    public static decimal ResolveMonthlyPrice(ServicePackage pkg, ServicePackageVariant? variant = null)
        => variant?.Price ?? pkg.Price;

    /// <summary>
    /// Full "due today" breakdown for the given package + billing day
    /// + selected variant, evaluated at <paramref name="nowUtc"/>.
    /// </summary>
    public static CheckoutBreakdown Compute(
        ServicePackage pkg,
        int billingDay,
        DateTime nowUtc,
        BillingSettings billingSettings,
        ServicePackageVariant? variant = null)
    {
        var activation = ResolveActivationFee(pkg, variant);
        if (!billingSettings.ChargeProRataAtCheckout(pkg.Type))
        {
            // Fibre-type packages: monthly meter starts after admin
            // activation, so no pro-rata is billed at checkout. The
            // post-activation pro-rata invoice is generated later by
            // OrderService.AdminActivateServiceAsync via FibreActivationProRataFactory.
            return new CheckoutBreakdown(activation, 0m, 0, null, null);
        }

        var quote = ProRataCalculator.Quote(ResolveMonthlyPrice(pkg, variant), nowUtc, billingDay);
        return new CheckoutBreakdown(
            activation,
            quote.ProRataAmount,
            quote.BillableDays,
            quote.BillableDays > 0 ? quote.StartDateUtc : null,
            quote.BillableDays > 0 ? quote.NextBillingDateUtc : null);
    }
}

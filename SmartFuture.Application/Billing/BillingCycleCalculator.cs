using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.Billing;

/// <summary>
/// Pure helper for deriving billing-cycle dates from a NetworkAccount.
/// We have no recurring-invoice generator yet, so the "next payment date"
/// surfaced by the API is computed from the activation date + cycle length.
/// Centralising the calculation here keeps the rule consistent across the
/// service-detail DTO, the billing overview endpoint, and any future job
/// that ends up materialising a real schedule.
/// </summary>
public static class BillingCycleCalculator
{
    // Monthly cycles use 30 days per the Phase 48 spec — calendar-month
    // anchors (e.g. "always the 5th") will arrive when the recurring-
    // invoice generator does.
    private static TimeSpan? CycleInterval(ServicePackageBillingCycle? cycle) => cycle switch
    {
        ServicePackageBillingCycle.Weekly    => TimeSpan.FromDays(7),
        ServicePackageBillingCycle.Monthly   => TimeSpan.FromDays(30),
        ServicePackageBillingCycle.Quarterly => TimeSpan.FromDays(90),
        ServicePackageBillingCycle.Yearly    => TimeSpan.FromDays(365),
        ServicePackageBillingCycle.OnceOff   => null,
        null                                  => TimeSpan.FromDays(30), // unknown cycle → assume monthly (safe default)
        _                                     => TimeSpan.FromDays(30)
    };

    /// <summary>
    /// Public accessor for the canonical cycle interval (Phase 0B+). The
    /// recurring billing engine uses this so the cycle-length rule
    /// (Monthly = 30 days, etc.) lives in exactly one place. Returns null
    /// for <see cref="ServicePackageBillingCycle.OnceOff"/> (no recurring
    /// billing).
    /// </summary>
    public static TimeSpan? IntervalFor(ServicePackageBillingCycle? cycle) => CycleInterval(cycle);

    /// <summary>
    /// Returns the next payment due date for an Active service, or null
    /// when the service isn't billable (Pending/Suspended/Terminated/
    /// Failed) or when the cycle is OnceOff. Rolls the activation date
    /// forward by `interval` until the result lies in the future, so
    /// long-running services don't keep returning the original
    /// "activation + 30 days" date.
    /// </summary>
    public static DateTime? ComputeNextPaymentDateUtc(NetworkAccount account, ServicePackageBillingCycle? cycleOverride = null)
    {
        if (account is null) return null;
        if (account.Status != NetworkAccountStatus.Active) return null;
        if (!account.ProvisionedAtUtc.HasValue) return null;

        var cycle = cycleOverride ?? account.Order?.PackageBillingCycle;
        var interval = CycleInterval(cycle);
        if (interval is null || interval.Value <= TimeSpan.Zero) return null;

        var next = account.ProvisionedAtUtc.Value + interval.Value;
        var now = DateTime.UtcNow;
        var guard = 0;
        while (next < now && guard++ < 120) next += interval.Value;
        return next;
    }

    /// <summary>
    /// Friendly status label for the billing card. The frontend prefers
    /// to drive its copy from a server-supplied label so this string
    /// stays in lockstep with the date semantics above.
    /// </summary>
    public static string BillingStatusLabel(NetworkAccount account) => account?.Status switch
    {
        NetworkAccountStatus.Active     => "Next payment due",
        NetworkAccountStatus.Pending    => "Billing starts after activation",
        NetworkAccountStatus.Suspended  => "Billing paused while suspended",
        NetworkAccountStatus.Terminated => "Service terminated",
        NetworkAccountStatus.Failed     => "Awaiting admin retry",
        null                             => "Unknown",
        _                                => "Unknown"
    };
}

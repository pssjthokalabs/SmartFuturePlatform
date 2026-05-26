using SmartFuture.Application.Billing;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.ServiceChanges;

/// <summary>
/// Phase 51 — pure helper for the upgrade pro-rata charge.
///
/// Formula (matches the user's brief):
///   <c>proRata = ((newPrice - currentPrice) / cycleDays) * remainingDays</c>
///
/// Where:
///   - <c>cycleDays</c> derives from the package billing cycle
///     (Monthly = 30 to match <see cref="BillingCycleCalculator"/>).
///   - <c>remainingDays</c> = ceil(daysBetween(today, nextPaymentDate)).
///
/// Downgrades return 0 — the customer paid for the current cycle so
/// they're not charged today; the downgrade just takes effect next
/// cycle.
///
/// Pure + side-effect-free so the controller can call it twice (once
/// for the preview endpoint, once during create) without worrying
/// about clocks drifting between calls.
/// </summary>
public static class ProRataCalculator
{
    private static int CycleDays(ServicePackageBillingCycle cycle) => cycle switch
    {
        ServicePackageBillingCycle.Weekly    => 7,
        ServicePackageBillingCycle.Monthly   => 30,
        ServicePackageBillingCycle.Quarterly => 90,
        ServicePackageBillingCycle.Yearly    => 365,
        ServicePackageBillingCycle.OnceOff   => 0,
        _                                    => 30
    };

    public class Result
    {
        public bool     IsUpgrade           { get; init; }
        public decimal  ProRataAmount       { get; init; }
        public int      CycleDays           { get; init; }
        public int      RemainingDays       { get; init; }
        public DateTime EffectiveDateUtc    { get; init; }
        public DateTime? NextCycleAnchorUtc { get; init; }
        public bool     Eligible            { get; init; }
        public string?  IneligibilityReason { get; init; }
    }

    /// <summary>
    /// Run the calc against an account's current state. The caller has
    /// already loaded the active <see cref="NetworkAccount"/> + decided
    /// the requested package — this helper just does the math.
    /// Returns an ineligible result if the account isn't actively
    /// billable; the calling service surfaces a friendly error in that
    /// case.
    /// </summary>
    public static Result Compute(
        NetworkAccount account,
        decimal currentMonthlyPrice,
        decimal requestedMonthlyPrice,
        ServicePackageBillingCycle currentCycle,
        DateTime nowUtc)
    {
        var isUpgrade = requestedMonthlyPrice > currentMonthlyPrice;
        var nextCycle = BillingCycleCalculator.ComputeNextPaymentDateUtc(account, currentCycle);

        if (nextCycle is null)
        {
            // Account isn't Active / has no provisioned date / OnceOff
            // package — pro-rata is not meaningful. Surface so the
            // caller can refuse politely.
            return new Result
            {
                IsUpgrade           = isUpgrade,
                ProRataAmount       = 0m,
                CycleDays           = 0,
                RemainingDays       = 0,
                EffectiveDateUtc    = nowUtc,
                NextCycleAnchorUtc  = null,
                Eligible            = false,
                IneligibilityReason = "This service is not currently in an active monthly billing cycle."
            };
        }

        var cycleDays     = CycleDays(currentCycle);
        var remainingDays = Math.Max(0, (int)Math.Ceiling((nextCycle.Value - nowUtc).TotalDays));

        if (!isUpgrade)
        {
            // Downgrade — no charge today, swap deferred to next cycle.
            return new Result
            {
                IsUpgrade          = false,
                ProRataAmount      = 0m,
                CycleDays          = cycleDays,
                RemainingDays      = remainingDays,
                EffectiveDateUtc   = nextCycle.Value,
                NextCycleAnchorUtc = nextCycle,
                Eligible           = true
            };
        }

        // Upgrade. Round to 2dp for currency. Floor at 0 so a same-day
        // upgrade (remainingDays=0, e.g. the customer's next cycle is
        // literally today) costs nothing today and just bumps the next
        // invoice — matches the brief's "pay the difference for the
        // unused remainder" intent.
        var diff = requestedMonthlyPrice - currentMonthlyPrice;
        var proRata = cycleDays <= 0
            ? 0m
            : Math.Round(diff * remainingDays / cycleDays, 2, MidpointRounding.AwayFromZero);
        if (proRata < 0m) proRata = 0m;

        return new Result
        {
            IsUpgrade          = true,
            ProRataAmount      = proRata,
            CycleDays          = cycleDays,
            RemainingDays      = remainingDays,
            EffectiveDateUtc   = nowUtc,
            NextCycleAnchorUtc = nextCycle,
            Eligible           = true
        };
    }
}

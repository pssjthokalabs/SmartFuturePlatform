using SmartFuture.Domain.Common;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Domain.Billing;

/// <summary>
/// Phase 0A — the per-service recurring-billing anchor. One row per
/// active billable service, carrying the "when is the next invoice / due
/// date" state the recurring engine needs (a gap called out in the audit:
/// <c>NetworkAccount</c> has no <c>NextBillingDateUtc</c>).
///
/// FOUNDATION ONLY in Phase 0A: this entity/table is created but nothing
/// reads or writes it yet. The recurring invoice generator (Phase 0B) is
/// the first writer/consumer; until then no schedule rows exist.
/// </summary>
public class ServiceBillingSchedule : BaseEntity
{
    public Guid NetworkAccountId { get; set; }
    public NetworkAccount? NetworkAccount { get; set; }

    public Guid OrderId { get; set; }
    public Guid UserId { get; set; }

    public ServicePackageBillingCycle BillingCycle { get; set; } = ServicePackageBillingCycle.Monthly;
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";

    /// <summary>Day-of-month the cycle is anchored to (derived from activation date by the future generator).</summary>
    public int AnchorDayOfMonth { get; set; }

    public DateTime? CurrentPeriodStartUtc { get; set; }
    public DateTime? CurrentPeriodEndUtc { get; set; }

    public DateTime? NextInvoiceDateUtc { get; set; }
    public DateTime? NextDueDateUtc { get; set; }

    /// <summary>Duplicate-period guard: the period end most recently invoiced for this service.</summary>
    public DateTime? LastInvoicedPeriodEndUtc { get; set; }
    public Guid? LastInvoiceId { get; set; }

    public ServiceBillingScheduleStatus Status { get; set; } = ServiceBillingScheduleStatus.Active;
    public bool IsAutoBillable { get; set; } = true;

    public string? Notes { get; set; }
}

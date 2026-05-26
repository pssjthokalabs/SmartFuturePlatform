using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Enums.ServiceChanges;

namespace SmartFuture.Domain.ServiceChanges;

// Phase 51 — customer-initiated upgrade/downgrade workflow row.
//
// Wraps the existing AdminChangeNetworkAccount package swap with a
// self-service workflow: pro-rata calc, customer payment (upgrades),
// or scheduled-for-next-cycle deferral (downgrades). One row per
// request, immutable once Completed/Cancelled/Rejected/Failed.
//
// Snapshot columns (CurrentPackage* + RequestedPackage*) freeze the
// prices + names at request time so admin reports stay correct even
// if a ServicePackage gets edited or retired afterwards. They are
// authoritative — never re-query the live ServicePackage for these.
public class ServiceChangeRequest : BaseEntity
{
    public string RequestNumber { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid NetworkAccountId { get; set; }
    public NetworkAccount? NetworkAccount { get; set; }

    // Snapshotted at creation. The active source Order pointer lets
    // admins drill into the install history; nullable so we can still
    // service requests if the original Order ever moves.
    public Guid? OrderId { get; set; }
    public Order? Order { get; set; }

    // Current = whatever the NetworkAccount was running at request
    // time. Requested = the target.
    public Guid?   CurrentPackageId  { get; set; }
    public string  CurrentPackageName { get; set; } = string.Empty;
    public decimal CurrentMonthlyPrice { get; set; }
    public ServicePackageBillingCycle CurrentBillingCycle { get; set; }

    public Guid RequestedPackageId { get; set; }
    public ServicePackage? RequestedPackage { get; set; }
    public string  RequestedPackageName { get; set; } = string.Empty;
    public decimal RequestedMonthlyPrice { get; set; }
    public ServicePackageBillingCycle RequestedBillingCycle { get; set; }

    public ServiceChangeType          ChangeType    { get; set; }
    public ServiceChangeEffectiveMode EffectiveMode { get; set; }
    public ServiceChangeStatus        Status        { get; set; } = ServiceChangeStatus.PendingPayment;
    public ServiceChangeSource        Source        { get; set; } = ServiceChangeSource.CustomerApp;

    // Pro-rata for upgrades — backend-calculated, zero for downgrades.
    // Snapshot so the customer always sees the same number, even if
    // they re-open the request after we've slipped past the next-cycle
    // anchor.
    public decimal ProRataAmount      { get; set; }
    public int     ProRataCycleDays   { get; set; }
    public int     ProRataRemainingDays { get; set; }

    /// <summary>
    /// Upgrades: today (the swap happens after payment).
    /// Downgrades: the next-cycle anchor date.
    /// </summary>
    public DateTime EffectiveDateUtc { get; set; }

    // Wired for upgrades: the pro-rata Invoice + Payment (when the
    // customer goes through mock-checkout/Ozow). Null for downgrades.
    public Guid? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public Guid? PaymentId { get; set; }
    public Payment? Payment { get; set; }

    public string? CustomerNotes { get; set; }
    public string? AdminNotes    { get; set; }

    public DateTime? AppliedAtUtc   { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? RejectedAtUtc  { get; set; }
    public DateTime? FailedAtUtc    { get; set; }

    public string? CancellationReason { get; set; }
    public string? RejectionReason    { get; set; }
    public string? FailureReason      { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public User? LastStatusChangedByUser   { get; set; }
}

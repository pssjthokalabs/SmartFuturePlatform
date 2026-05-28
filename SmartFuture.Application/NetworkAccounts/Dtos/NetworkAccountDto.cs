using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.NetworkAccounts.Dtos;

// Phase 46 — small installation summary attached to NetworkAccountDto
// detail responses so the service detail page can render the linked
// installation status / scheduled date without a second round-trip.
public class NetworkAccountInstallationSummaryDto
{
    public Guid Id { get; set; }
    public string InstallationNumber { get; set; } = string.Empty;
    public InstallationStatus Status { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public class NetworkAccountDto
{
    public Guid Id { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public OrderStatus? OrderStatus { get; set; }
    public Guid? UserId { get; set; }

    public NetworkAccountStatus Status { get; set; }
    public NetworkAccountSource Source { get; set; }

    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }

    public ServicePackageType PackageType { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string? PackageSpeedLabel { get; set; }
    public decimal PackagePrice { get; set; }

    // Phase 46 — extra package snapshot fields surfaced from the linked
    // Order so admin/client service detail pages can render the same
    // package overview the order detail does.
    public string? PackageDataAllowanceLabel { get; set; }
    public bool? PackageIsUncapped { get; set; }
    public ServicePackageBillingCycle? PackageBillingCycle { get; set; }
    public int? PackageContractMonths { get; set; }
    public bool? PackageHasFreeInstallation { get; set; }
    public decimal? PackageInstallationFee { get; set; }
    public bool? PackageIncludesRouter { get; set; }

    // Phase 46 — customer snapshot (admin-only consumer; the /mine
    // endpoint already scopes to the authenticated user so this just
    // mirrors data the customer already knows).
    public string? CustomerFullName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhoneNumber { get; set; }

    // Phase 46 — service address copied from the Order at projection
    // time. The Order's address is the source of truth.
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }

    public DateTime? ProvisionedAtUtc { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }
    public DateTime? ResumedAtUtc { get; set; }
    public DateTime? TerminatedAtUtc { get; set; }
    public DateTime? LastPackageChangeAtUtc { get; set; }

    public string? AdminNotes { get; set; }
    public string? LastFailureReason { get; set; }
    public string? SuspensionReason { get; set; }
    public string? TerminationReason { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public string? LastStatusChangedByUserEmail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public ProvisioningStatus ProvisioningStatus { get; set; }
    public DateTime? LastProvisioningAttemptUtc { get; set; }
    public int ProvisioningAttemptCount { get; set; }
    public Guid? RadiusProfileId { get; set; }
    public string? RadiusProfileName { get; set; }
    public string? CurrentIpAddress { get; set; }
    public string? NasIdentifier { get; set; }

    // Phase 3.5 — provisioning-ready snapshot from the linked
    // ServicePackage so the admin Network Account detail panel can
    // surface whether the underlying package is actually wired for
    // RADIUS automation. Read-only; never set on requests.
    public bool? PackageRequiresProvisioning { get; set; }
    public ProvisioningType? PackageProvisioningType { get; set; }
    public int? PackageDownloadSpeedMbps { get; set; }
    public int? PackageUploadSpeedMbps { get; set; }
    public int? PackageBurstSpeedMbps { get; set; }

    // Phase 46 — installation summary. Populated only on detail
    // (get-by-id) responses to keep list payloads lean.
    public NetworkAccountInstallationSummaryDto? Installation { get; set; }

    // Phase 48 — computed billing-cycle fields. There is no recurring
    // invoice engine yet; these are derived from `ProvisionedAtUtc +
    // PackageBillingCycle` via BillingCycleCalculator. See that helper
    // for the per-status semantics (null for Pending/Terminated, etc.).
    public DateTime? NextPaymentDateUtc { get; set; }
    public decimal? NextPaymentAmount { get; set; }
    public string? BillingStatusLabel { get; set; }
}

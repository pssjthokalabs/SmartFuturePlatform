using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.ServicePackages.Dtos;

public class UpdateServicePackageRequestDto
{
    public ServicePackageType Type { get; set; }
    // Desired status. When null the existing row's status is preserved
    // (legacy behaviour). When set, the service applies it inline so an
    // admin who edits "Status" in the form doesn't need a second round-
    // trip to /activate or /deactivate. Archived is rejected here — the
    // dedicated /archive endpoint owns the irreversible transition.
    public ServicePackageStatus? Status { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }

    public string? SpeedLabel { get; set; }
    public int? DownloadSpeedMbps { get; set; }
    public int? UploadSpeedMbps { get; set; }

    public string? DataAllowanceLabel { get; set; }
    public bool IsUncapped { get; set; }

    public decimal Price { get; set; }
    public ServicePackageBillingCycle BillingCycle { get; set; } = ServicePackageBillingCycle.Monthly;
    public int? ContractMonths { get; set; }

    public bool HasFreeInstallation { get; set; }
    public decimal? InstallationFee { get; set; }

    public bool IncludesRouter { get; set; }
    public string? RouterDescription { get; set; }

    public bool IsFeatured { get; set; }
    public int DisplayOrder { get; set; }

    public string? TermsSummary { get; set; }
    public string? CoverageNotes { get; set; }
    public string? ExternalReference { get; set; }

    public bool RequiresProvisioning { get; set; }
    public ProvisioningType? ProvisioningType { get; set; }
    public int? BurstSpeedMbps { get; set; }
    public Guid? RadiusProfileId { get; set; }

    public string? ImageUrl { get; set; }
    public string? ImageStorageKey { get; set; }

    // Admin-configured marketing feature bullets. Optional.
    public List<string>? Features { get; set; }

    // Optional DB-driven sub-category (Security → CCTV/Intercom). Only
    // honoured for Security packages. Send null to clear; the service
    // always applies the field on update (the admin form sends it).
    public Guid? SubTypeId { get; set; }

    // Full desired variant set. Null = leave variants untouched (legacy
    // callers). Non-null = the service reconciles: upsert by Id, delete
    // the rows the admin removed. An empty list clears all variants.
    public List<ServicePackageVariantInputDto>? Variants { get; set; }
}

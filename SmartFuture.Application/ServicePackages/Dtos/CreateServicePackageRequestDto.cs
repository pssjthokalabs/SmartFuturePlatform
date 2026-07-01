using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.ServicePackages.Dtos;

public class CreateServicePackageRequestDto
{
    public ServicePackageType Type { get; set; }
    // Initial status. When null the service defaults to Draft so legacy
    // callers (and the back-office bulk-import path) keep their old
    // "save in draft, promote later" behaviour. Admins choosing Active
    // in the form post `Status = Active` and the row lands publishable
    // immediately — Archived is rejected at this entry-point because it
    // has its own dedicated transition endpoint.
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
    // honoured for Security packages; validated against an active subtype
    // of the same PackageType.
    public Guid? SubTypeId { get; set; }

    // Optional orderable variants ("4 IP" / "8 IP"). Null/empty → the
    // package has no variants and keeps its own pricing.
    public List<ServicePackageVariantInputDto>? Variants { get; set; }
}

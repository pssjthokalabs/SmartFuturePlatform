using SmartFuture.Domain.Common;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Domain.ServicePackages;

public class ServicePackage : BaseEntity
{
    public ServicePackageType Type { get; set; }
    public ServicePackageStatus Status { get; set; } = ServicePackageStatus.Draft;

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
    public RadiusProfile? RadiusProfile { get; set; }
}

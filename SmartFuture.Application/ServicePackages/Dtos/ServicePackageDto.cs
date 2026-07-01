using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.ServicePackages.Dtos;

public class ServicePackageDto
{
    public Guid Id { get; set; }
    public ServicePackageType Type { get; set; }
    public ServicePackageStatus Status { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }

    public string? SpeedLabel { get; set; }
    public int? DownloadSpeedMbps { get; set; }
    public int? UploadSpeedMbps { get; set; }

    public string? DataAllowanceLabel { get; set; }
    public bool IsUncapped { get; set; }

    public decimal Price { get; set; }
    public ServicePackageBillingCycle BillingCycle { get; set; }
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
    public string? RadiusProfileName { get; set; }

    public string? ImageUrl { get; set; }
    public string? ImageStorageKey { get; set; }

    // Admin-configured marketing feature bullets. Empty when none set.
    public List<string> Features { get; set; } = new();

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

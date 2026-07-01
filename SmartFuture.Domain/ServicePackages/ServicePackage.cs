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

    // Marketing image rendered on the public catalogue (Security CCTV
    // packages always have one; fibre packages may have one). Uploaded
    // via the admin form and stored in Cloudflare R2 — ImageUrl is the
    // public CDN URL, ImageStorageKey is the object key inside the
    // bucket (kept so we can delete/rename on overwrite).
    public string? ImageUrl { get; set; }
    public string? ImageStorageKey { get; set; }

    // Admin-configured marketing feature bullets (e.g. "4MP Camera",
    // "Night Vision"). Persisted as a JSON string array; the application
    // layer serializes/deserializes to List<string>. Null/empty means the
    // package has no configured features (the public UI hides the bar).
    public string? FeaturesJson { get; set; }

    // Optional DB-driven sub-category (CCTV / Intercom / …). Nullable so
    // Fibre and legacy Security packages stay valid; the public site
    // falls back to "CCTV" for Security packages with no subtype.
    public Guid? SubTypeId { get; set; }
    public ServicePackageSubType? SubType { get; set; }

    // Optional orderable variants (e.g. "4 IP" / "8 IP"). Empty for
    // packages that don't use variants — those keep their own pricing.
    public ICollection<ServicePackageVariant> Variants { get; set; } = new List<ServicePackageVariant>();
}

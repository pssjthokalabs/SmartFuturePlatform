using SmartFuture.Domain.Common;

namespace SmartFuture.Domain.ServicePackages;

// An orderable option under a single ServicePackage — e.g. one CCTV
// package offering "4 IP" and "8 IP" variants at different monthly
// prices. Variants are OPTIONAL: a package with none continues to behave
// exactly as before (its own Price / InstallationFee / HasFreeInstallation
// drive checkout). When a variant is selected at checkout its overrides
// take precedence and are snapshotted onto the Order.
public class ServicePackageVariant : BaseEntity
{
    public Guid ServicePackageId { get; set; }
    public ServicePackage? ServicePackage { get; set; }

    // Customer-facing label (e.g. "4 IP"). Unique (case-insensitive)
    // within a package — enforced in the application layer.
    public string Name { get; set; } = string.Empty;

    // Monthly price for this variant. Always required (a variant that
    // didn't change the price wouldn't be a variant); overrides the
    // package price at checkout.
    public decimal Price { get; set; }

    // Optional activation-once-off-fee override. Null → inherit the
    // package's InstallationFee at checkout.
    public decimal? InstallationFee { get; set; }

    // Optional free-activation override. Null → inherit the package's
    // HasFreeInstallation flag at checkout.
    public bool? HasFreeInstallation { get; set; }

    // Only active variants are shown publicly / selectable at checkout.
    // Admin can disable a variant without deleting it.
    public bool IsActive { get; set; } = true;

    public int DisplayOrder { get; set; }
}

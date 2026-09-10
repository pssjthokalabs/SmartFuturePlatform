using SmartFuture.Domain.Common;
using SmartFuture.Domain.ServicePackages;

namespace SmartFuture.Domain.Openserve;

// Durable SmartFuture ServicePackage -> Openserve product mapping
// (brief §4). One row per ServicePackage. IsEnabled defaults to false
// deliberately: an unmapped or not-yet-verified package must never
// silently submit to Openserve — the submission trigger checks this
// flag, not just row existence.
public class PackageOpenserveMapping : BaseEntity
{
    public Guid ServicePackageId { get; set; }
    public ServicePackage? ServicePackage { get; set; }

    /// <summary>Must match productOffering.name exactly, e.g. "Openserve Fibre Connect".</summary>
    public string OpenserveProductName { get; set; } = string.Empty;

    /// <summary>e.g. "OFC" — see Appendix D for the full SKU catalogue.</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>e.g. "75" — must be one of Appendix D's valid capacities for this SKU.</summary>
    public string Capacity { get; set; } = string.Empty;

    public string CapacityUom { get; set; } = "Mbps";

    /// <summary>False by default. An admin must explicitly confirm a mapping before it can be used at submission time.</summary>
    public bool IsEnabled { get; set; }

    public string? OpenserveProductOfferingId { get; set; }
    public string? OpenserveProductSpecificationId { get; set; }

    public string? Notes { get; set; }
}

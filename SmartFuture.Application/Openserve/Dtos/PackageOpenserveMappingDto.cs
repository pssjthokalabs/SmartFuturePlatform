namespace SmartFuture.Application.Openserve.Dtos;

public class PackageOpenserveMappingDto
{
    public Guid Id { get; set; }
    public Guid ServicePackageId { get; set; }
    public string ServicePackageName { get; set; } = string.Empty;
    public string OpenserveProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Capacity { get; set; } = string.Empty;
    public string CapacityUom { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? OpenserveProductOfferingId { get; set; }
    public string? OpenserveProductSpecificationId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// One row per ACTIVE Fibre ServicePackage without an ENABLED mapping —
/// exactly the packages whose orders the submission gate would block
/// (brief §4: "An unmapped package must never silently submit to
/// Openserve"). A disabled mapping counts as unavailable.
/// </summary>
public class UnmappedServicePackageDto
{
    public Guid ServicePackageId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? DownloadSpeedMbps { get; set; }
    public string? SpeedLabel { get; set; }

    /// <summary>"Unmapped" (no mapping row) | "Disabled" (row exists, IsEnabled = false).</summary>
    public string MappingStatus { get; set; } = PackageOpenserveMappingStatus.Unmapped;
}

public static class PackageOpenserveMappingStatus
{
    public const string Mapped = "Mapped";
    public const string Unmapped = "Unmapped";
    public const string Disabled = "Disabled";
}

/// <summary>One Fibre ServicePackage with its Openserve mapping (if any) — the Package Mappings table.</summary>
public class FibrePackageMappingRowDto
{
    public Guid ServicePackageId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>ServicePackageStatus name: Draft | Active | Inactive | Archived.</summary>
    public string PackageStatus { get; set; } = string.Empty;
    public string? SpeedLabel { get; set; }
    public int? DownloadSpeedMbps { get; set; }
    public int? UploadSpeedMbps { get; set; }
    public decimal Price { get; set; }
    public string BillingCycle { get; set; } = string.Empty;

    /// <summary>True for Active Fibre packages — the only ones Readiness and the submission gate require a mapping for.</summary>
    public bool RequiredForReadiness { get; set; }

    /// <summary>"Mapped" | "Unmapped" | "Disabled".</summary>
    public string MappingStatus { get; set; } = PackageOpenserveMappingStatus.Unmapped;

    public PackageOpenserveMappingDto? Mapping { get; set; }
}

/// <summary>One orderable product/speed from Appendix D — the only values the mapping editor offers.</summary>
public class OpenserveCatalogueSpeedDto
{
    public string Capacity { get; set; } = string.Empty;
    public string CapacityUom { get; set; } = string.Empty;
    public bool IsRetentionOffer { get; set; }
    /// <summary>False for retention offers — Openserve accepts them only as a Regrade, never on a new Sales Order.</summary>
    public bool OrderableAsNewSalesOrder { get; set; }
}

public class OpenserveCatalogueProductDto
{
    public string Sku { get; set; } = string.Empty;
    /// <summary>productOffering.name exactly as Openserve documents it.</summary>
    public string ProductName { get; set; } = string.Empty;
    /// <summary>Appendix D technology: FIBRE | COPPER | MICROWAVE | SATELLITE; null when Appendix D has no speed table for the SKU.</summary>
    public string? Technology { get; set; }
    public bool HasPublishedSpeedTable { get; set; }
    public List<OpenserveCatalogueSpeedDto> Speeds { get; set; } = new();
}

public class SetPackageOpenserveMappingEnabledRequestDto
{
    public bool IsEnabled { get; set; }
}

public class CreatePackageOpenserveMappingRequestDto
{
    public Guid ServicePackageId { get; set; }
    public string OpenserveProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Capacity { get; set; } = string.Empty;
    public string CapacityUom { get; set; } = "Mbps";
    public bool IsEnabled { get; set; }
    public string? OpenserveProductOfferingId { get; set; }
    public string? OpenserveProductSpecificationId { get; set; }
    public string? Notes { get; set; }
}

public class UpdatePackageOpenserveMappingRequestDto
{
    public string OpenserveProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Capacity { get; set; } = string.Empty;
    public string CapacityUom { get; set; } = "Mbps";
    public bool IsEnabled { get; set; }
    public string? OpenserveProductOfferingId { get; set; }
    public string? OpenserveProductSpecificationId { get; set; }
    public string? Notes { get; set; }
}

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
/// One row per fibre ServicePackage that has NO mapping yet — surfaced
/// so admin can see at a glance which packages would be silently
/// rejected at submission time (brief §4: "An unmapped package must
/// never silently submit to Openserve").
/// </summary>
public class UnmappedServicePackageDto
{
    public Guid ServicePackageId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? DownloadSpeedMbps { get; set; }
    public string? SpeedLabel { get; set; }
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

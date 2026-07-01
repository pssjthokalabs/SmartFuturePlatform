using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.ServicePackages.Dtos;

public class ServicePackageSubTypeDto
{
    public Guid Id { get; set; }
    public ServicePackageType PackageType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int DisplayOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateServicePackageSubTypeRequestDto
{
    public ServicePackageType PackageType { get; set; } = ServicePackageType.Security;
    public string Name { get; set; } = string.Empty;
    // Optional — the service slugifies Name when this is blank.
    public string? Slug { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
}

public class UpdateServicePackageSubTypeRequestDto
{
    public string Name { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
}

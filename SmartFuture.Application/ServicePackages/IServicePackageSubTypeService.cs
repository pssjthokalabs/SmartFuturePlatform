using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.ServicePackages;

public interface IServicePackageSubTypeService
{
    // List subtypes, optionally scoped to a package line (Security). When
    // activeOnly is true, disabled subtypes are hidden (the "new package"
    // dropdown uses this); admin management pages pass false.
    Task<Result<List<ServicePackageSubTypeDto>>> ListAsync(
        ServicePackageType? packageType,
        bool activeOnly,
        CancellationToken cancellationToken = default);

    Task<Result<ServicePackageSubTypeDto>> CreateAsync(
        CreateServicePackageSubTypeRequestDto request,
        CancellationToken cancellationToken = default);

    Task<Result<ServicePackageSubTypeDto>> UpdateAsync(
        Guid id,
        UpdateServicePackageSubTypeRequestDto request,
        CancellationToken cancellationToken = default);
}

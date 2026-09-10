using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

public interface IPackageOpenserveMappingService
{
    Task<Result<IReadOnlyList<PackageOpenserveMappingDto>>> ListAsync(CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<UnmappedServicePackageDto>>> ListUnmappedFibrePackagesAsync(CancellationToken cancellationToken = default);

    Task<Result<PackageOpenserveMappingDto>> GetByServicePackageIdAsync(Guid servicePackageId, CancellationToken cancellationToken = default);

    Task<Result<PackageOpenserveMappingDto>> CreateAsync(CreatePackageOpenserveMappingRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<PackageOpenserveMappingDto>> UpdateAsync(Guid id, UpdatePackageOpenserveMappingRequestDto request, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

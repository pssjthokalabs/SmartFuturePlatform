using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

public interface IPackageOpenserveMappingService
{
    Task<Result<IReadOnlyList<PackageOpenserveMappingDto>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Active Fibre packages without an ENABLED mapping (no row, or row disabled) — what Readiness and the submission gate treat as unmapped.</summary>
    Task<Result<IReadOnlyList<UnmappedServicePackageDto>>> ListUnmappedFibrePackagesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every Fibre package with its mapping status. Active only unless <paramref name="includeNonActive"/> (Draft/Inactive); Archived never.</summary>
    Task<Result<IReadOnlyList<FibrePackageMappingRowDto>>> ListFibrePackageMappingsAsync(bool includeNonActive = false, CancellationToken cancellationToken = default);

    /// <summary>The Appendix D product/speed catalogue the mapping editor offers.</summary>
    IReadOnlyList<OpenserveCatalogueProductDto> GetCatalogue();

    Task<Result<PackageOpenserveMappingDto>> SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default);

    Task<Result<PackageOpenserveMappingDto>> GetByServicePackageIdAsync(Guid servicePackageId, CancellationToken cancellationToken = default);

    Task<Result<PackageOpenserveMappingDto>> CreateAsync(CreatePackageOpenserveMappingRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<PackageOpenserveMappingDto>> UpdateAsync(Guid id, UpdatePackageOpenserveMappingRequestDto request, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

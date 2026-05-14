using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.ServicePackages;

public interface IServicePackageService
{
    Task<Result<PagedResult<ServicePackageDto>>> SearchAdminAsync(
        ServicePackageFilterRequestDto filter,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<ServicePackageDto>>> SearchCustomerAsync(
        ServicePackageFilterRequestDto filter,
        CancellationToken cancellationToken = default);

    Task<Result<ServicePackageDto>> GetAdminByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<ServicePackageDto>> GetCustomerByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<ServicePackageDto>> CreateAsync(
        CreateServicePackageRequestDto request,
        CancellationToken cancellationToken = default);

    Task<Result<ServicePackageDto>> UpdateAsync(
        Guid id,
        UpdateServicePackageRequestDto request,
        CancellationToken cancellationToken = default);

    Task<Result> ActivateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result> DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result> ArchiveAsync(Guid id, CancellationToken cancellationToken = default);
}

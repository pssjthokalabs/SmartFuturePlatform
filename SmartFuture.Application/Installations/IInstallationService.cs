using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Installations.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Installations;

public interface IInstallationService
{
    Task<Result<PagedResult<InstallationDto>>> SearchAdminAsync(
        InstallationFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<InstallationDto>>> GetMineAsync(
        InstallationFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<InstallationDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<InstallationDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<InstallationDto>> CreateAsync(
        CreateInstallationRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<InstallationDto>> AdminUpdateAsync(
        Guid id, AdminUpdateInstallationRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<InstallationDto>> AdminUpdateStatusAsync(
        Guid id, AdminUpdateInstallationStatusDto request, CancellationToken cancellationToken = default);
}

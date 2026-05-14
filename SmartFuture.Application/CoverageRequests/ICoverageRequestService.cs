using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.CoverageRequests.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.CoverageRequests;

public interface ICoverageRequestService
{
    Task<Result<PagedResult<CoverageRequestDto>>> SearchAdminAsync(
        CoverageRequestFilterRequestDto filter,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<CoverageRequestDto>>> GetMineAsync(
        CoverageRequestFilterRequestDto filter,
        CancellationToken cancellationToken = default);

    Task<Result<CoverageRequestDto>> GetAdminByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<CoverageRequestDto>> GetMineByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<CoverageRequestDto>> CreateMineAsync(
        CreateCoverageRequestDto request,
        CancellationToken cancellationToken = default);

    Task<Result<CoverageRequestDto>> AdminUpdateAsync(
        Guid id,
        AdminUpdateCoverageRequestDto request,
        CancellationToken cancellationToken = default);

    Task<Result<CoverageRequestDto>> AdminUpdateStatusAsync(
        Guid id,
        AdminUpdateCoverageRequestStatusDto request,
        CancellationToken cancellationToken = default);

    Task<Result> CancelMineAsync(Guid id, CancellationToken cancellationToken = default);
}

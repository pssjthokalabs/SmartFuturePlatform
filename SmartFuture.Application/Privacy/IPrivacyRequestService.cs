using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Privacy.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Privacy;

public interface IPrivacyRequestService
{
    Task<Result<PagedResult<PrivacyRequestDto>>> SearchAdminAsync(
        PrivacyRequestFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<PrivacyRequestDto>>> GetMineAsync(
        PrivacyRequestFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PrivacyRequestDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<PrivacyRequestDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PrivacyRequestDto>> CreateMineAsync(
        CreatePrivacyRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<PrivacyRequestDto>> AdminUpdateStatusAsync(
        Guid id, AdminUpdatePrivacyRequestStatusDto request, CancellationToken cancellationToken = default);

    Task<Result<UserErasureResultDto>> AdminEraseUserAsync(
        AdminEraseUserRequestDto request, CancellationToken cancellationToken = default);
}

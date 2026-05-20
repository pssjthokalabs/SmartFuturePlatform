using SmartFuture.Application.Users.Admin.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Users.Admin;

public interface IAdminUsersService
{
    Task<Result<AdminUsersSearchResultDto>> SearchAsync(AdminUsersFilterRequestDto filter, CancellationToken cancellationToken = default);
    Task<Result<AdminUserListItemDto>> CreateAsync(CreateAdminUserRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<AdminUserListItemDto>> UpdateAsync(Guid id, UpdateAdminUserRequestDto request, CancellationToken cancellationToken = default);
}

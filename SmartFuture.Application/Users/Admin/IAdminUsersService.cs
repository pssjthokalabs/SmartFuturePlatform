using SmartFuture.Application.Users.Admin.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Users.Admin;

public interface IAdminUsersService
{
    Task<Result<AdminUsersSearchResultDto>> SearchAsync(AdminUsersFilterRequestDto filter, CancellationToken cancellationToken = default);
    Task<Result<AdminUserListItemDto>> CreateAsync(CreateAdminUserRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<AdminUserListItemDto>> UpdateAsync(Guid id, UpdateAdminUserRequestDto request, CancellationToken cancellationToken = default);

    // Phase 56 — SuperAdmin-only "change role / change user type" flow.
    // Strips every Identity role from the target and re-assigns the
    // single role requested. Returns the updated user DTO so the
    // portal can refresh its row without a second round-trip.
    Task<Result<AdminUserListItemDto>> ChangeRoleAsync(Guid id, ChangeAdminUserRoleRequestDto request, CancellationToken cancellationToken = default);
}

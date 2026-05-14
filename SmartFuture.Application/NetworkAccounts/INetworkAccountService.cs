using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.NetworkAccounts;

public interface INetworkAccountService
{
    Task<Result<PagedResult<NetworkAccountDto>>> SearchAdminAsync(
        NetworkAccountFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<NetworkAccountDto>>> GetMineAsync(
        NetworkAccountFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<NetworkAccountDto>> GetAdminByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<Result<NetworkAccountDto>> GetMineByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotent provisioning entry point used by hooks (Installation Completed,
    /// Payment Applied) and by the admin manual-provision endpoint.
    /// Performs eligibility checks based on PackageType and Order state.
    /// </summary>
    Task<Result<NetworkAccountDto>> ProvisionForOrderAsync(
        Guid orderId, NetworkAccountSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Terminates all non-terminated accounts attached to the given order.
    /// Used by OrderService when an order transitions to a cancelled/failed/rejected state.
    /// Best-effort: provider termination failures are logged but do not abort the caller.
    /// </summary>
    Task<Result> TerminateForOrderAsync(
        Guid orderId, string? reason, NetworkAccountSource source,
        CancellationToken cancellationToken = default);

    Task<Result<NetworkAccountDto>> AdminSuspendAsync(
        Guid id, AdminSuspendNetworkAccountRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<NetworkAccountDto>> AdminResumeAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<Result<NetworkAccountDto>> AdminTerminateAsync(
        Guid id, AdminTerminateNetworkAccountRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<NetworkAccountDto>> AdminChangePackageAsync(
        Guid id, AdminChangeNetworkAccountPackageRequestDto request, CancellationToken cancellationToken = default);
}

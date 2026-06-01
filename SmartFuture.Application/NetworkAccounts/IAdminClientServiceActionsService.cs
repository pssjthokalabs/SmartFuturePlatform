using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.NetworkAccounts;

/// <summary>
/// Thin orchestrator the admin Client Service Detail "Activate Service"
/// button calls. Lives outside <see cref="INetworkAccountService"/>
/// because it composes <see cref="INetworkAccountService"/>,
/// <see cref="Payments.IAutoBillingService"/>, and
/// <see cref="Orders.IOrderService"/> — putting it on
/// <c>INetworkAccountService</c> would create a DI cycle.
/// </summary>
public interface IAdminClientServiceActionsService
{
    Task<Result<AdminActivateOrSettleResultDto>> ActivateOrSettleAsync(
        Guid networkAccountId, CancellationToken cancellationToken = default);
}

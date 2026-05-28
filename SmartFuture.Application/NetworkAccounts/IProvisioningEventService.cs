using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.NetworkAccounts;

public interface IProvisioningEventService
{
    Task<Result<PagedResult<ProvisioningEventDto>>> SearchAsync(
        ProvisioningEventFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<ProvisioningEventDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

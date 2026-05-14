using SmartFuture.Application.CustomerProfiles.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.CustomerProfiles;

public interface ICustomerProfileService
{
    Task<Result<CustomerProfileDto>> GetMineAsync(Guid userId);
    Task<Result<CustomerProfileDto>> CreateOrUpdateMineAsync(Guid userId, UpdateCustomerProfileRequestDto request);
}

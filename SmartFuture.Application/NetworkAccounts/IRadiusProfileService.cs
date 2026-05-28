using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.NetworkAccounts;

public interface IRadiusProfileService
{
    Task<Result<IReadOnlyList<RadiusProfileDto>>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default);
    Task<Result<RadiusProfileDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<RadiusProfileDto>> CreateAsync(CreateRadiusProfileRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<RadiusProfileDto>> UpdateAsync(Guid id, UpdateRadiusProfileRequestDto request, CancellationToken cancellationToken = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

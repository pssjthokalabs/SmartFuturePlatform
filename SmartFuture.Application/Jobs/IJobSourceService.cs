using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs;

public interface IJobSourceService
{
    Task<Result<PagedResult<JobSourceDto>>> SearchAsync(JobSourceFilterRequestDto filter, CancellationToken cancellationToken = default);
    Task<Result<JobSourceDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<JobSourceDto>> CreateAsync(CreateJobSourceRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<JobSourceDto>> UpdateAsync(Guid id, UpdateJobSourceRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<JobSourceDto>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);
}

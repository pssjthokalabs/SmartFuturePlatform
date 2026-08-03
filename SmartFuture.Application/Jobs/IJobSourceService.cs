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

    // What a permanent delete would remove. Never modifies anything.
    Task<Result<JobSourceDeletePreviewDto>> GetDeletePreviewAsync(Guid id, CancellationToken cancellationToken = default);

    // Permanently removes the source and the jobs it imported.
    // Manually-edited jobs are PRESERVED unless the caller explicitly
    // opts in; preserved jobs are detached (SourceId = null) and keep
    // their SourceName/SourceUrl snapshot so provenance survives.
    // Import runs are always kept as history.
    Task<Result<JobSourceDeleteResultDto>> DeleteAsync(Guid id, bool deleteManuallyEditedJobs, CancellationToken cancellationToken = default);
}

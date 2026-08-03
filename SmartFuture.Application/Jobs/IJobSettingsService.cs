using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs;

public interface IJobSettingsService
{
    // Reads (and lazily creates) the singleton settings row. Every other
    // job service calls this rather than touching the table directly.
    Task<JobModuleSettings> GetOrCreateAsync(CancellationToken cancellationToken = default);

    Task<Result<JobSettingsDto>> GetAsync(CancellationToken cancellationToken = default);
    Task<Result<PublicJobSettingsDto>> GetPublicAsync(CancellationToken cancellationToken = default);
    Task<Result<JobSettingsDto>> UpdateAsync(UpdateJobSettingsRequestDto request, CancellationToken cancellationToken = default);
}

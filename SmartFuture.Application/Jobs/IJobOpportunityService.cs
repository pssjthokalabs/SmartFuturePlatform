using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs;

public interface IJobOpportunityService
{
    // ─── Public surface ───────────────────────────────────────────────
    Task<Result<PagedResult<JobOpportunityDto>>> SearchPublicAsync(JobOpportunityFilterRequestDto filter, CancellationToken cancellationToken = default);

    // `callerIsSubscriber` decides whether the gated detail fields are
    // populated when the module is configured subscribers-only. The
    // controller resolves it from the caller's roles.
    Task<Result<JobOpportunityDto>> GetPublicBySlugOrIdAsync(string slugOrId, bool callerIsSubscriber, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<JobCategoryDto>>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<JobLocationDto>>> GetLocationsAsync(CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<JobSourceFacetDto>>> GetSourceFacetsAsync(CancellationToken cancellationToken = default);

    // ─── Admin surface ────────────────────────────────────────────────
    Task<Result<PagedResult<AdminJobOpportunityDto>>> SearchAdminAsync(AdminJobOpportunityFilterRequestDto filter, CancellationToken cancellationToken = default);
    Task<Result<AdminJobOpportunityDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<AdminJobOpportunityDto>> CreateAsync(CreateJobOpportunityRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<AdminJobOpportunityDto>> UpdateAsync(Guid id, UpdateJobOpportunityRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<AdminJobOpportunityDto>> PublishAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<AdminJobOpportunityDto>> HideAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<AdminJobOpportunityDto>> ExpireAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<AdminJobOpportunityDto>> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // Sweep that flips Active rows whose closing date has passed to
    // Expired. Called by the importer and available to the admin as a
    // one-click maintenance action. Returns the number of rows changed.
    Task<Result<int>> ExpireClosedJobsAsync(CancellationToken cancellationToken = default);
}

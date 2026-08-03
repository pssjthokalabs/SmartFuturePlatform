using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs;

// Job alert newsletter — foundation.
//
// Scope of this phase: build a per-subscriber digest of matching jobs
// and queue it through the EXISTING notification pipeline, with a
// delivery-log row for every outcome (including Skipped). It is
// admin-triggered and gated by JobModuleSettings.JobAlertsEnabled;
// no background scheduler is wired yet, deliberately, so alerts cannot
// start mailing subscribers the moment the module is deployed.
public interface IJobAlertService
{
    Task<Result<JobAlertRunSummaryDto>> RunDigestAsync(JobAlertFrequency frequency, bool dryRun, CancellationToken cancellationToken = default);

    // Preview what ONE subscriber would receive right now. Sends
    // nothing — used by the admin detail page to sanity-check a
    // subscriber's filters.
    Task<Result<IReadOnlyList<JobOpportunityDto>>> PreviewMatchesAsync(Guid userId, JobAlertFrequency frequency, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<JobAlertDeliveryLogDto>>> SearchDeliveryLogsAsync(Guid? userId, int? page, int? pageSize, CancellationToken cancellationToken = default);
}

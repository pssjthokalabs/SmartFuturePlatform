using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs.Import;

public interface IJobImportService
{
    // Crawl one configured source. Always returns a Result — a source
    // that blocks us produces a SUCCESSFUL call carrying a failed run,
    // because "we tried and were refused" is information, not an error
    // in our own system.
    Task<Result<JobImportRunDto>> RunSourceAsync(Guid sourceId, JobImportRunTrigger trigger, CancellationToken cancellationToken = default);

    // Crawl every active source. `onlyDue` honours each source's
    // CrawlFrequencyMinutes — used by the scheduled worker; the admin's
    // "Refresh all" passes false.
    Task<Result<JobImportSummaryDto>> RunAllAsync(JobImportRunTrigger trigger, bool onlyDue, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<JobImportRunDto>>> SearchRunsAsync(JobImportRunFilterRequestDto filter, CancellationToken cancellationToken = default);
}

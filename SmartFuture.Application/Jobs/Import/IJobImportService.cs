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

    // Manual refresh, asynchronous. Validates the source, reuses an
    // already-Running run if one exists (so double-clicking Refresh
    // cannot start two crawls of the same source), otherwise creates a
    // Running run and queues it for the background worker. Returns as
    // soon as the row is written — the crawl itself never touches the
    // HTTP request.
    Task<Result<JobImportRunDto>> QueueSourceRefreshAsync(Guid sourceId, JobImportRunTrigger trigger, CancellationToken cancellationToken = default);

    // Executed by the background worker for a run that QueueSourceRefreshAsync
    // already created. Never throws: every outcome ends as a persisted
    // run row, because the caller is a worker with nobody to report to.
    Task ExecuteQueuedRunAsync(Guid sourceId, Guid runId, CancellationToken cancellationToken = default);

    // Fails any run left Running longer than the background duration
    // limit. A crawl is only ever abandoned mid-flight by process death
    // (app-pool recycle), which leaves a row that would otherwise block
    // that source's duplicate-run check forever.
    Task<Result<int>> ReapStuckRunsAsync(CancellationToken cancellationToken = default);

    Task<Result<JobImportRunDto>> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<JobImportRunDto>>> SearchRunsAsync(JobImportRunFilterRequestDto filter, CancellationToken cancellationToken = default);

    // Repair hatch for rows a previous crawl got wrong — most obviously
    // the whole-archive-as-one-job records the listing classifier now
    // prevents. Removes only untouched imported rows for one source;
    // anything an admin edited by hand is left alone, so this can be run
    // without losing curated content. Re-running the source then
    // re-imports the same listings correctly.
    Task<Result<int>> PurgeImportedJobsAsync(Guid sourceId, CancellationToken cancellationToken = default);
}

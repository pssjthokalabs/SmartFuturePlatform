using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs.Import;

// Orchestrates fetch → extract → reconcile → log for job sources.
//
// Failure policy (the important part): ONE bad source must never abort a
// run or crash the worker. Every source is wrapped, every outcome —
// including "403 Forbidden" and "page was gibberish" — is written to a
// JobImportRun row and reflected on the source's health fields. The
// caller gets a summary, not an exception.
public class JobImportService : IJobImportService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    // Notes column is nvarchar(4000); leave headroom for the suffix.
    private const int MaxNotesLength = 3500;
    // Hard stop on archive paging regardless of what a source asks for.
    // Kept in step with JobSourceService's validation ceiling.
    private const int MaxPagesPerRunCeiling = 50;

    /// <summary>
    /// How long a background import may run before it is abandoned, and
    /// how old a <c>Running</c> row must be before the reaper fails it.
    ///
    /// This is a BACKGROUND limit, not an HTTP one — nothing is waiting
    /// on it, so it can be generous. It exists for two reasons: a
    /// pathological source should not crawl forever, and a run orphaned
    /// by an app-pool recycle must eventually stop blocking that
    /// source's duplicate-run check.
    /// </summary>
    public static readonly TimeSpan BackgroundRunLimit = TimeSpan.FromMinutes(30);

    private readonly IAppDbContext _dbContext;
    private readonly IJobSourceFetcher _fetcher;
    private readonly IJobContentExtractor _extractor;
    private readonly IJobOpportunityService _jobService;
    private readonly IJobImportQueue _queue;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<JobImportService> _logger;

    public JobImportService(IAppDbContext dbContext, IJobSourceFetcher fetcher, IJobContentExtractor extractor, IJobOpportunityService jobService,
        IJobImportQueue queue, IAuditService auditService, ICurrentUserService currentUser, ILogger<JobImportService> logger)
    {
        _dbContext = dbContext;
        _fetcher = fetcher;
        _extractor = extractor;
        _jobService = jobService;
        _queue = queue;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    // ─── Async manual refresh ─────────────────────────────────────────

    public async Task<Result<JobImportRunDto>> QueueSourceRefreshAsync(Guid sourceId, JobImportRunTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        if (sourceId == Guid.Empty)
            return Result<JobImportRunDto>.Failure(ErrorCodes.BAD_REQUEST, "Source id is required.");

        var source = await _dbContext.JobSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
            return Result<JobImportRunDto>.Failure(ErrorCodes.NOT_FOUND, "Job source not found.");

        if (source.SourceType == JobSourceType.Manual)
            return Result<JobImportRunDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Manual sources are not crawled. Capture jobs for this source by hand.");

        // Clear out zombies first — otherwise a run orphaned by a
        // process restart would make this source permanently "already
        // running" and no refresh could ever start again.
        await ReapStuckRunsAsync(cancellationToken);

        // DUPLICATE PROTECTION: one crawl per source at a time. A second
        // click gets the run that is already going, not a second crawler
        // competing with the first over the same rows.
        var inFlight = await _dbContext.JobImportRuns
            .Where(r => r.SourceId == sourceId && r.Status == JobImportRunStatus.Running)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (inFlight is not null)
        {
            return Result<JobImportRunDto>.Success(MapRunToDto(inFlight),
                "An import is already running for this source. Tracking the run that is already in progress.");
        }

        var run = new JobImportRun
        {
            SourceId = source.Id,
            SourceName = source.SourceName,
            Trigger = trigger,
            TriggeredByUserId = _currentUser.UserId,
            StartedAtUtc = DateTime.UtcNow,
            Status = JobImportRunStatus.Running
        };

        _dbContext.JobImportRuns.Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!_queue.TryEnqueue(new JobImportQueueItem(source.Id, run.Id)))
        {
            // Queue full. Fail the row immediately rather than leaving it
            // Running forever with nothing to process it.
            run.Status = JobImportRunStatus.Failed;
            run.IsSuccess = false;
            run.CompletedAtUtc = DateTime.UtcNow;
            run.FailureMessage = "The import queue is full. Wait for the running imports to finish, then try again.";
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<JobImportRunDto>.Failure(ErrorCodes.TOO_MANY_REQUESTS, run.FailureMessage);
        }

        _logger.LogInformation("[job-import] queued manual refresh for source {SourceId} ({SourceName}) as run {RunId}",
            source.Id, source.SourceName, run.Id);

        return Result<JobImportRunDto>.Success(MapRunToDto(run),
            "Import started. You can track progress in import history.");
    }

    public async Task ExecuteQueuedRunAsync(Guid sourceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.JobImportRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            _logger.LogWarning("[job-import] queued run {RunId} no longer exists; nothing to do.", runId);
            return;
        }

        // Already finished — most likely the reaper failed it while it sat
        // in the queue behind a long crawl. Do not resurrect it.
        if (run.Status != JobImportRunStatus.Running)
        {
            _logger.LogInformation("[job-import] queued run {RunId} is already {Status}; skipping.", runId, run.Status);
            return;
        }

        var source = await _dbContext.JobSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
        {
            run.Status = JobImportRunStatus.Failed;
            run.IsSuccess = false;
            run.CompletedAtUtc = DateTime.UtcNow;
            run.FailureMessage = "The job source was deleted before the queued import started.";
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        // The background duration limit. Not an HTTP deadline — nothing is
        // waiting on this — so it only guards against a pathological source.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(BackgroundRunLimit);

        try
        {
            await ExecuteSourceRunAsync(source, run.Trigger, cts.Token, existingRun: run);

            var expired = await _jobService.ExpireClosedJobsAsync(CancellationToken.None);
            if (expired.IsSuccess && expired.Data > 0)
            {
                run.JobsExpired = expired.Data;
                await _dbContext.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // ExecuteSourceRunAsync has already written the run row with a
            // message and its partial counts before rethrowing. Nothing is
            // waiting on this call, so the exception stops here.
            _logger.LogWarning("[job-import] run {RunId} for source {SourceId} stopped before finishing.", runId, sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[job-import] run {RunId} for source {SourceId} failed unexpectedly.", runId, sourceId);
        }
    }

    public async Task<Result<int>> ReapStuckRunsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoff = DateTime.UtcNow - BackgroundRunLimit;
            var stuck = await _dbContext.JobImportRuns
                .Where(r => r.Status == JobImportRunStatus.Running && r.StartedAtUtc < cutoff)
                .ToListAsync(cancellationToken);

            if (stuck.Count == 0) return Result<int>.Success(0);

            foreach (var run in stuck)
            {
                run.Status = JobImportRunStatus.Failed;
                run.IsSuccess = false;
                run.CompletedAtUtc = DateTime.UtcNow;
                run.DurationMs = (int)Math.Min(int.MaxValue, (run.CompletedAtUtc.Value - run.StartedAtUtc).TotalMilliseconds);
                run.FailureMessage = Truncate(
                    $"Import did not report back within {BackgroundRunLimit.TotalMinutes:F0} minutes and was marked failed. " +
                    "This usually means the API restarted while the crawl was running. Any jobs imported before that point were kept. " +
                    "Refresh again to resume.", 2000);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("[job-import] reaped {Count} stuck run(s).", stuck.Count);
            return Result<int>.Success(stuck.Count, $"Marked {stuck.Count} stalled import run(s) as failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reaping stuck job import runs");
            return Result<int>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while cleaning up stalled import runs.");
        }
    }

    public async Task<Result<JobImportRunDto>> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
            return Result<JobImportRunDto>.Failure(ErrorCodes.BAD_REQUEST, "Run id is required.");

        var run = await _dbContext.JobImportRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        return run is null
            ? Result<JobImportRunDto>.Failure(ErrorCodes.NOT_FOUND, "Import run not found.")
            : Result<JobImportRunDto>.Success(MapRunToDto(run));
    }

    public async Task<Result<JobImportRunDto>> RunSourceAsync(Guid sourceId, JobImportRunTrigger trigger, CancellationToken cancellationToken = default)
    {
        if (sourceId == Guid.Empty)
            return Result<JobImportRunDto>.Failure(ErrorCodes.BAD_REQUEST, "Source id is required.");

        var source = await _dbContext.JobSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
            return Result<JobImportRunDto>.Failure(ErrorCodes.NOT_FOUND, "Job source not found.");

        if (source.SourceType == JobSourceType.Manual)
            return Result<JobImportRunDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Manual sources are not crawled. Capture jobs for this source by hand.");

        var run = await ExecuteSourceRunAsync(source, trigger, cancellationToken);

        // Housekeeping the admin expects after any refresh: anything past
        // its closing date stops being publicly listed.
        var expired = await _jobService.ExpireClosedJobsAsync(cancellationToken);
        if (expired.IsSuccess && expired.Data > 0)
        {
            run.JobsExpired = expired.Data;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Result<JobImportRunDto>.Success(MapRunToDto(run),
            run.IsSuccess
                ? $"Imported {run.JobsCreated} new and updated {run.JobsUpdated} existing job(s)."
                : $"Import failed: {run.FailureMessage}");
    }

    public async Task<Result<JobImportSummaryDto>> RunAllAsync(JobImportRunTrigger trigger, bool onlyDue, CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var sources = await _dbContext.JobSources
                .Where(s => s.IsActive && s.SourceType != JobSourceType.Manual)
                .OrderBy(s => s.LastCheckedAtUtc ?? DateTime.MinValue)
                .ToListAsync(cancellationToken);

            if (onlyDue)
            {
                // A source with no configured frequency is manual-refresh
                // only and is skipped by the scheduled path.
                sources = sources.Where(s => s.CrawlFrequencyMinutes.HasValue
                    && (s.LastCheckedAtUtc is null || s.LastCheckedAtUtc.Value.AddMinutes(s.CrawlFrequencyMinutes.Value) <= now)).ToList();
            }

            var runs = new List<JobImportRun>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runs.Add(await ExecuteSourceRunAsync(source, trigger, cancellationToken));
            }

            var expired = await _jobService.ExpireClosedJobsAsync(cancellationToken);
            var expiredCount = expired.IsSuccess ? expired.Data : 0;

            var summary = new JobImportSummaryDto
            {
                SourcesAttempted = runs.Count,
                SourcesSucceeded = runs.Count(r => r.IsSuccess),
                SourcesFailed = runs.Count(r => !r.IsSuccess),
                JobsCreated = runs.Sum(r => r.JobsCreated),
                JobsUpdated = runs.Sum(r => r.JobsUpdated),
                JobsSkipped = runs.Sum(r => r.JobsSkipped),
                JobsExpired = expiredCount,
                Runs = runs.Select(MapRunToDto).ToList()
            };

            var message = runs.Count == 0
                ? (onlyDue ? "No sources were due for a refresh." : "No active crawlable sources are configured.")
                : $"{summary.SourcesSucceeded}/{summary.SourcesAttempted} source(s) refreshed. {summary.JobsCreated} new, {summary.JobsUpdated} updated.";

            return Result<JobImportSummaryDto>.Success(summary, message);
        }
        catch (OperationCanceledException)
        {
            // Reached either on host shutdown (scheduled runs) or when a
            // caller's deadline fired. Each source finishes and persists
            // its own run row before rethrowing, so JobImportRuns shows
            // exactly which source was in flight and how far it got.
            return Result<JobImportSummaryDto>.Failure(ErrorCodes.EXCEPTION,
                "The import stopped before finishing every source — either the API is shutting down or a caller's deadline fired. " +
                "See the JobImportRuns log for the source that was in flight and how far it got.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error running job import for all sources");
            return Result<JobImportSummaryDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while running the job import.");
        }
    }

    // ─── One source, fully guarded ────────────────────────────────────

    // `existingRun` is supplied by the background worker: the refresh
    // endpoint already wrote a Running row so the admin had a runId to
    // poll the moment the request returned. Adopt that row rather than
    // creating a second one, or the history would show two entries per
    // refresh and the duplicate-run check would never clear.
    private async Task<JobImportRun> ExecuteSourceRunAsync(JobSource source, JobImportRunTrigger trigger, CancellationToken cancellationToken,
        JobImportRun? existingRun = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var run = existingRun ?? new JobImportRun
        {
            SourceId = source.Id,
            SourceName = source.SourceName,
            Trigger = trigger,
            TriggeredByUserId = _currentUser.UserId,
            StartedAtUtc = DateTime.UtcNow,
            Status = JobImportRunStatus.Running
        };

        if (existingRun is null) _dbContext.JobImportRuns.Add(run);
        source.LastCheckedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var notes = new List<string>();

        try
        {
            var fetch = await _fetcher.FetchAsync(source.SourceUrl, cancellationToken);
            run.HttpStatusCode = fetch.StatusCode;

            if (!fetch.IsSuccess)
            {
                await FinishRunAsync(run, source, stopwatch, success: false, fetch.FailureMessage ?? "Fetch failed.", notes, cancellationToken);
                return run;
            }

            var pageUrl = JobTextUtilities.NullIfBlank(fetch.FinalUrl) ?? source.SourceUrl;
            var analysis = _extractor.Analyze(source, fetch.Content ?? string.Empty, fetch.ContentType, pageUrl);

            var candidates = analysis.IsListing
                ? await CollectFromListingAsync(source, analysis, run, notes, cancellationToken)
                : CollectFromDetailPage(source, fetch.Content ?? string.Empty, fetch.ContentType, pageUrl, notes);

            run.JobsFound = candidates.Count;

            if (candidates.Count == 0)
            {
                // Not a crash — some pages genuinely have nothing today,
                // and some block us with a 200-status interstitial. Both
                // are recorded as a failed run so the source's health
                // reflects that it is not producing.
                await FinishRunAsync(run, source, stopwatch, success: false,
                    analysis.IsListing
                        ? "The listing page was fetched but none of its job links could be parsed."
                        : "The page was fetched but no job listings could be extracted from it.",
                    notes, cancellationToken);
                return run;
            }

            var capped = candidates.Take(Math.Max(1, source.MaxJobsPerRun)).ToList();
            if (candidates.Count > capped.Count)
                notes.Add($"Source returned {candidates.Count} jobs; capped at MaxJobsPerRun={source.MaxJobsPerRun}.");

            foreach (var candidate in capped)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var outcome = await UpsertJobAsync(source, candidate, notes, cancellationToken);
                    switch (outcome)
                    {
                        case UpsertOutcome.Created: run.JobsCreated++; break;
                        case UpsertOutcome.Updated: run.JobsUpdated++; break;
                        default: run.JobsSkipped++; break;
                    }
                }
                catch (Exception ex)
                {
                    // One malformed listing must not lose the other 49.
                    run.JobsSkipped++;
                    notes.Add($"Skipped '{Truncate(candidate.Title, 60)}': {ex.Message}");
                    _logger.LogWarning(ex, "Failed to import a job from source {SourceId}", source.Id);
                }
            }

            await FinishRunAsync(run, source, stopwatch, success: true, null, notes, cancellationToken);
            return run;
        }
        catch (OperationCanceledException)
        {
            // WHY THIS RETHROWS
            //
            // This used to swallow the cancellation and return a normal
            // run object carrying "The import run was cancelled." That
            // made the caller's own handling unreachable: the refresh
            // endpoint has a 60s crawl deadline and a catch that turns it
            // into an actionable message, but the exception never got
            // there, so the admin only ever saw "cancelled" with no cause.
            //
            // The run record is still finished and persisted first (with
            // CancellationToken.None, so the write survives the very
            // cancellation that got us here) — the crawl budget it used is
            // exactly the evidence needed to tell "too slow" from "too
            // large". THEN it rethrows so the caller can say what the
            // deadline actually was.
            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            await FinishRunAsync(run, source, stopwatch, success: false,
                $"Stopped after {elapsedSeconds:F0}s before the crawl finished. " +
                $"Budget for this source is MaxPagesPerRun={source.MaxPagesPerRun}, MaxJobsPerRun={source.MaxJobsPerRun} " +
                $"— up to {EstimatedFetchCount(source)} sequential page fetches at up to {FetchTimeoutSeconds}s each. " +
                "Lower those limits, or run this source as a scheduled background import.",
                notes, CancellationToken.None);

            _logger.LogWarning(
                "[job-import] source {SourceId} ({SourceName}) cancelled after {ElapsedMs}ms with maxPages={MaxPages} maxJobs={MaxJobs}; " +
                "created={Created} updated={Updated} skipped={Skipped}",
                source.Id, source.SourceName, run.DurationMs, source.MaxPagesPerRun, source.MaxJobsPerRun,
                run.JobsCreated, run.JobsUpdated, run.JobsSkipped);

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error importing job source {SourceId}", source.Id);
            await FinishRunAsync(run, source, stopwatch, success: false, $"Unexpected error: {ex.Message}", notes, CancellationToken.None);
            return run;
        }
    }

    // The refresh crawl is strictly sequential: one fetch per listing page,
    // then one fetch per job link found. Both are bounded by the source's
    // own limits, so the worst-case request length is predictable — and is
    // what decides whether a source can finish inside a web request at all.
    private static int EstimatedFetchCount(JobSource source)
        => Math.Clamp(source.MaxPagesPerRun, 1, MaxPagesPerRunCeiling) + Math.Max(1, source.MaxJobsPerRun);

    // Mirrors the typed HttpClient registration in ServiceExtensions
    // (AddHttpClient<IJobSourceFetcher, HttpJobSourceFetcher>).
    private const int FetchTimeoutSeconds = 20;

    // ─── Listing vs detail ────────────────────────────────────────────

    private List<ExtractedJob> CollectFromDetailPage(JobSource source, string content, string? contentType, string pageUrl, List<string> notes)
    {
        var extraction = _extractor.Extract(source, content, contentType, DateTime.UtcNow, pageUrl);
        notes.AddRange(extraction.Notes);
        notes.Add($"Extraction strategy: {extraction.StrategyUsed}.");
        return extraction.Jobs;
    }

    // Walks an archive: collect post URLs from this page (and optionally
    // the next ones), then fetch and parse each post on its own.
    //
    // The archive page itself never becomes a JobOpportunity. That is
    // the whole point — importing it produced one row whose title was
    // the category name and whose description was the entire page.
    private async Task<List<ExtractedJob>> CollectFromListingAsync(JobSource source, JobPageAnalysis firstPage, JobImportRun run,
        List<string> notes, CancellationToken cancellationToken)
    {
        var maxJobs = Math.Max(1, source.MaxJobsPerRun);
        var maxPages = Math.Clamp(source.MaxPagesPerRun, 1, MaxPagesPerRunCeiling);

        var childUrls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var page = firstPage;
        var pagesWalked = 1;
        var linksFound = page.TotalChildLinksFound;
        notes.AddRange(page.Notes);
        AddChildUrls(page.ChildUrls, childUrls, seen, maxJobs);

        while (pagesWalked < maxPages && childUrls.Count < maxJobs && !string.IsNullOrWhiteSpace(page.NextPageUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nextUrl = page.NextPageUrl!;
            var nextFetch = await _fetcher.FetchAsync(nextUrl, cancellationToken);
            if (!nextFetch.IsSuccess)
            {
                notes.Add($"Stopped paging at {nextUrl}: {nextFetch.FailureMessage}");
                break;
            }

            pagesWalked++;
            page = _extractor.Analyze(source, nextFetch.Content ?? string.Empty, nextFetch.ContentType,
                JobTextUtilities.NullIfBlank(nextFetch.FinalUrl) ?? nextUrl);
            linksFound += page.TotalChildLinksFound;
            AddChildUrls(page.ChildUrls, childUrls, seen, maxJobs);
        }

        var hitJobLimit = childUrls.Count >= maxJobs && linksFound > childUrls.Count;
        var hitPageLimit = pagesWalked >= maxPages && !string.IsNullOrWhiteSpace(page.NextPageUrl);

        var jobs = new List<ExtractedJob>();
        var fetched = 0;
        var fetchFailed = 0;

        foreach (var url in childUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var detail = await _fetcher.FetchAsync(url, cancellationToken);
            if (!detail.IsSuccess)
            {
                run.JobsSkipped++;
                fetchFailed++;
                notes.Add($"Could not fetch {url}: {detail.FailureMessage}");
                continue;
            }

            fetched++;

            var detailUrl = JobTextUtilities.NullIfBlank(detail.FinalUrl) ?? url;
            var extraction = _extractor.Extract(source, detail.Content ?? string.Empty, detail.ContentType, DateTime.UtcNow, detailUrl);

            if (extraction.Jobs.Count == 0)
            {
                run.JobsSkipped++;
                notes.Add($"No job could be parsed from {url}.");
                continue;
            }

            foreach (var job in extraction.Jobs)
            {
                job.SourceUrl = JobTextUtilities.NullIfBlank(job.SourceUrl) ?? detailUrl;
                jobs.Add(job);
            }
        }

        // The crawl budget, spelled out. An admin who sees "5 imported"
        // must be able to tell from the log alone whether that is all
        // the source had or the point where a cap stopped us.
        notes.Insert(0, BuildCrawlSummary(maxPages, maxJobs, pagesWalked, page.NextPageUrl, linksFound,
            childUrls.Count, fetched, fetchFailed, hitJobLimit, hitPageLimit));

        return jobs;
    }

    private static string BuildCrawlSummary(int maxPages, int maxJobs, int pagesWalked, string? nextPageUrl,
        int linksFound, int queued, int fetched, int fetchFailed, bool hitJobLimit, bool hitPageLimit)
    {
        var lines = new List<string>
        {
            "── Crawl summary ──",
            $"Crawl budget           : up to {maxPages} archive page(s) and {maxJobs} job detail page(s) per refresh.",
            $"Archive pages walked   : {pagesWalked} of {maxPages} allowed.",
            $"Next page detected     : {(string.IsNullOrWhiteSpace(nextPageUrl) ? "no" : $"yes ({nextPageUrl})")}",
            $"Child job links found  : {linksFound}",
            $"Detail jobs queued     : {queued}" + (linksFound > queued ? $"  ({linksFound - queued} left behind)" : string.Empty),
            $"Detail pages fetched   : {fetched}" + (fetchFailed > 0 ? $"  ({fetchFailed} failed)" : string.Empty),
            $"Stopped by MaxJobsPerRun : {(hitJobLimit ? "YES" : "no")}",
            $"Stopped by MaxPagesPerRun: {(hitPageLimit ? "YES" : "no")}"
        };

        if (hitJobLimit)
        {
            lines.Add($"→ This source had {linksFound} job(s) available but MaxJobsPerRun is {maxJobs}. "
                + $"Raise MaxJobsPerRun to import the remaining {linksFound - queued}.");
        }

        if (hitPageLimit)
        {
            lines.Add($"→ More archive pages exist but MaxPagesPerRun is {maxPages}. "
                + "Raise MaxPagesPerRun to follow the next page.");
        }

        return string.Join('\n', lines);
    }

    private static void AddChildUrls(IEnumerable<string> incoming, List<string> target, HashSet<string> seen, int max)
    {
        foreach (var url in incoming)
        {
            if (target.Count >= max) return;
            if (seen.Add(JobListingLinkExtractor.NormalizeForComparison(url))) target.Add(url);
        }
    }

    private enum UpsertOutcome { Created, Updated, Skipped }

    private async Task<UpsertOutcome> UpsertJobAsync(JobSource source, ExtractedJob candidate, List<string> notes, CancellationToken cancellationToken)
    {
        var title = JobTextUtilities.NullIfBlank(candidate.Title);
        if (title is null)
        {
            notes.Add("Skipped a listing with no title.");
            return UpsertOutcome.Skipped;
        }

        var sourceUrl = JobTextUtilities.NullIfBlank(candidate.SourceUrl) ?? source.SourceUrl;
        var fingerprint = JobTextUtilities.ComputeFingerprint(sourceUrl, title, candidate.CompanyName, candidate.Location);

        var existing = await _dbContext.JobOpportunities.FirstOrDefaultAsync(j => j.Fingerprint == fingerprint, cancellationToken);
        var now = DateTime.UtcNow;

        if (existing is not null)
        {
            // Always refresh "we still see this listing", even for rows
            // an admin has edited or hidden.
            existing.LastSeenAtUtc = now;

            if (existing.IsManuallyEdited)
            {
                notes.Add($"'{Truncate(title, 60)}' is manually edited — only LastSeen refreshed.");
                await _dbContext.SaveChangesAsync(cancellationToken);
                return UpsertOutcome.Skipped;
            }

            // A soft-deleted job must NOT resurrect itself on the next
            // crawl. That is the whole reason Deleted is a status rather
            // than a row removal.
            if (existing.Status == JobOpportunityStatus.Deleted)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return UpsertOutcome.Skipped;
            }

            ApplyCandidateToEntity(existing, candidate, source, sourceUrl, refreshStatus: true);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return UpsertOutcome.Updated;
        }

        var entity = new JobOpportunity
        {
            Fingerprint = fingerprint,
            Slug = await BuildUniqueSlugAsync(title, candidate.CompanyName, cancellationToken),
            SourceId = source.Id,
            SourceName = source.SourceName,
            ImportedAtUtc = now,
            LastSeenAtUtc = now,
            // Draft when the source requires review; the admin publishes.
            Status = source.AutoPublish ? JobOpportunityStatus.Active : JobOpportunityStatus.Draft
        };

        ApplyCandidateToEntity(entity, candidate, source, sourceUrl, refreshStatus: false);

        // Never publish something that is already closed.
        if (entity.Status == JobOpportunityStatus.Active && entity.ClosingDateUtc is not null && entity.ClosingDateUtc < now)
            entity.Status = JobOpportunityStatus.Expired;

        _dbContext.JobOpportunities.Add(entity);
        source.TotalJobsImported++;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two runs raced on the same fingerprint (or slug). The
            // unique index did its job — treat as a skip rather than
            // failing the whole source.
            _dbContext.JobOpportunities.Entry(entity).State = EntityState.Detached;
            source.TotalJobsImported--;
            notes.Add($"Skipped '{Truncate(title, 60)}' — a matching job already exists.");
            return UpsertOutcome.Skipped;
        }

        return UpsertOutcome.Created;
    }

    // Copies extractor output onto an entity. Deliberately does NOT
    // clear an existing value when the extractor produced null — a
    // source that briefly stops publishing salary shouldn't wipe what we
    // already know.
    private static void ApplyCandidateToEntity(JobOpportunity entity, ExtractedJob candidate, JobSource source, string? sourceUrl, bool refreshStatus)
    {
        entity.Title = candidate.Title!.Trim();
        entity.CompanyName = JobTextUtilities.NullIfBlank(candidate.CompanyName) ?? entity.CompanyName;

        var (location, city, province) = JobFieldParsers.ParseLocation(candidate.Location ?? source.DefaultLocation);
        var resolvedCity = city ?? candidate.City;
        var resolvedProvince = province ?? candidate.Province;

        // Store what a reader should see, not the sentence the location
        // happened to be found in.
        entity.Location = JobFieldParsers.BuildDisplayLocation(location, resolvedCity, resolvedProvince) ?? entity.Location;
        entity.City = resolvedCity ?? entity.City;
        entity.Province = resolvedProvince ?? entity.Province;
        entity.Country = JobTextUtilities.NullIfBlank(candidate.Country) ?? entity.Country ?? "South Africa";

        entity.Category = JobTextUtilities.NullIfBlank(candidate.Category) ?? source.DefaultCategory ?? entity.Category;
        if (candidate.WorkplaceType != JobWorkplaceType.Unknown) entity.WorkplaceType = candidate.WorkplaceType;
        entity.EmploymentType = JobTextUtilities.NullIfBlank(candidate.EmploymentType) ?? entity.EmploymentType;
        entity.SalaryText = JobTextUtilities.NullIfBlank(candidate.SalaryText) ?? entity.SalaryText;

        entity.DescriptionHtml = JobTextUtilities.NullIfBlank(candidate.DescriptionHtml) ?? entity.DescriptionHtml;
        entity.DescriptionText = JobTextUtilities.NullIfBlank(candidate.DescriptionText) ?? entity.DescriptionText;
        entity.RequirementsText = JobTextUtilities.NullIfBlank(candidate.RequirementsText) ?? entity.RequirementsText;
        entity.Summary = JobTextUtilities.NullIfBlank(candidate.Summary)
            ?? entity.Summary
            ?? JobTextUtilities.BuildSummary(entity.DescriptionText);

        entity.ApplicationInstructions = JobTextUtilities.NullIfBlank(candidate.ApplicationInstructions) ?? entity.ApplicationInstructions;
        entity.ApplyUrl = JobTextUtilities.NullIfBlank(candidate.ApplyUrl) ?? entity.ApplyUrl;
        entity.ApplyEmail = JobTextUtilities.NullIfBlank(candidate.ApplyEmail) ?? entity.ApplyEmail;

        entity.SourceUrl = JobTextUtilities.NullIfBlank(sourceUrl) ?? entity.SourceUrl;
        entity.ExternalId = JobTextUtilities.NullIfBlank(candidate.ExternalId) ?? entity.ExternalId;
        entity.LogoUrl = JobTextUtilities.NullIfBlank(candidate.LogoUrl) ?? entity.LogoUrl;
        if (candidate.Tags.Count > 0) entity.TagsJson = JobTextUtilities.WriteStringList(candidate.Tags);

        entity.PostedDateUtc = candidate.PostedDateUtc ?? entity.PostedDateUtc;
        entity.ClosingDateUtc = candidate.ClosingDateUtc ?? entity.ClosingDateUtc;
        entity.RawContentSnapshot = candidate.RawContentSnapshot ?? entity.RawContentSnapshot;

        if (refreshStatus)
        {
            var now = DateTime.UtcNow;

            // A previously-expired listing that the source re-opened (new
            // closing date in the future) becomes live again — but only
            // if the source auto-publishes and an admin never hid it.
            if (entity.Status == JobOpportunityStatus.Expired
                && source.AutoPublish
                && (entity.ClosingDateUtc is null || entity.ClosingDateUtc >= now))
            {
                entity.Status = JobOpportunityStatus.Active;
            }
            else if (entity.Status == JobOpportunityStatus.Active && entity.ClosingDateUtc is not null && entity.ClosingDateUtc < now)
            {
                entity.Status = JobOpportunityStatus.Expired;
            }
        }
    }

    private async Task<string> BuildUniqueSlugAsync(string title, string? companyName, CancellationToken cancellationToken)
    {
        var baseSlug = JobTextUtilities.ToSlug(string.IsNullOrWhiteSpace(companyName) ? title : $"{title} {companyName}");
        var taken = await _dbContext.JobOpportunities.AsNoTracking()
            .Where(j => j.Slug == baseSlug || j.Slug.StartsWith(baseSlug + "-"))
            .Select(j => j.Slug)
            .ToListAsync(cancellationToken);

        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        return JobTextUtilities.BuildUniqueSlug(title, companyName, candidate => set.Contains(candidate));
    }

    // Writes the run's terminal state AND the source's health fields in
    // one save, so the Sources page and the run log can never disagree.
    private async Task FinishRunAsync(JobImportRun run, JobSource source, Stopwatch stopwatch, bool success, string? failureMessage, List<string> notes,
        CancellationToken cancellationToken)
    {
        stopwatch.Stop();

        run.CompletedAtUtc = DateTime.UtcNow;
        run.DurationMs = (int)stopwatch.ElapsedMilliseconds;
        run.IsSuccess = success;
        run.Status = success
            ? (run.JobsSkipped > 0 && run.JobsCreated == 0 && run.JobsUpdated == 0 ? JobImportRunStatus.PartiallySucceeded : JobImportRunStatus.Succeeded)
            : JobImportRunStatus.Failed;
        run.FailureMessage = Truncate(failureMessage, 2000);
        run.Notes = BuildNotes(notes);

        if (success)
        {
            source.LastSuccessAtUtc = run.CompletedAtUtc;
            source.ConsecutiveFailureCount = 0;
            source.LastFailureMessage = null;
        }
        else
        {
            source.LastFailureAtUtc = run.CompletedAtUtc;
            source.LastFailureMessage = run.FailureMessage;
            source.ConsecutiveFailureCount++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = _currentUser.UserId.HasValue ? AuditActorType.User : AuditActorType.System,
            ActionType = AuditActionType.JobImportRunCompleted,
            EntityType = AuditEntityType.JobSource,
            EntityId = source.Id,
            EntityName = source.SourceName,
            Summary = success
                ? $"Job import from {source.SourceName}: {run.JobsCreated} created, {run.JobsUpdated} updated, {run.JobsSkipped} skipped."
                : $"Job import from {source.SourceName} failed: {run.FailureMessage}",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = success
        });

        _logger.LogInformation(
            "[JobImport] source={Source} success={Success} found={Found} created={Created} updated={Updated} skipped={Skipped} durationMs={Duration}",
            source.SourceName, success, run.JobsFound, run.JobsCreated, run.JobsUpdated, run.JobsSkipped, run.DurationMs);
    }

    private static string? BuildNotes(List<string> notes)
    {
        if (notes.Count == 0) return null;

        var joined = string.Join('\n', notes);
        if (joined.Length <= MaxNotesLength) return joined;
        return joined[..MaxNotesLength] + $"\n… ({notes.Count} notes total, truncated)";
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    // ─── Repair ───────────────────────────────────────────────────────

    public async Task<Result<int>> PurgeImportedJobsAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (sourceId == Guid.Empty)
                return Result<int>.Failure(ErrorCodes.BAD_REQUEST, "Source id is required.");

            var source = await _dbContext.JobSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
            if (source is null)
                return Result<int>.Failure(ErrorCodes.NOT_FOUND, "Job source not found.");

            // Hand-edited rows survive: an admin who fixed a bad import
            // by hand should not lose that work to a cleanup.
            var doomed = await _dbContext.JobOpportunities
                .Where(j => j.SourceId == sourceId && !j.IsManuallyEdited)
                .ToListAsync(cancellationToken);

            if (doomed.Count == 0)
                return Result<int>.Success(0, "There were no imported jobs to remove for this source.");

            _dbContext.JobOpportunities.RemoveRange(doomed);
            source.TotalJobsImported = Math.Max(0, source.TotalJobsImported - doomed.Count);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = _currentUser.UserId.HasValue ? AuditActorType.User : AuditActorType.System,
                ActionType = AuditActionType.JobImportRunCompleted,
                EntityType = AuditEntityType.JobSource,
                EntityId = source.Id,
                EntityName = source.SourceName,
                Summary = $"Purged {doomed.Count} imported job(s) from {source.SourceName} for re-import.",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            _logger.LogInformation("[JobImport] purged {Count} imported jobs from source {Source}", doomed.Count, source.SourceName);

            return Result<int>.Success(doomed.Count,
                $"Removed {doomed.Count} imported job(s). Refresh the source to re-import them cleanly.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error purging imported jobs for source {SourceId}", sourceId);
            return Result<int>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while removing imported jobs.");
        }
    }

    // ─── Run log ──────────────────────────────────────────────────────

    public async Task<Result<PagedResult<JobImportRunDto>>> SearchRunsAsync(JobImportRunFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new JobImportRunFilterRequestDto();
            var page = Math.Max(1, filter.Page ?? 1);
            var pageSize = Math.Clamp(filter.PageSize ?? DefaultPageSize, 1, MaxPageSize);

            var query = _dbContext.JobImportRuns.AsNoTracking().AsQueryable();

            if (filter.SourceId.HasValue) query = query.Where(r => r.SourceId == filter.SourceId.Value);
            if (filter.Status.HasValue) query = query.Where(r => r.Status == filter.Status.Value);
            if (filter.IsSuccess.HasValue) query = query.Where(r => r.IsSuccess == filter.IsSuccess.Value);

            var totalCount = await query.CountAsync(cancellationToken);
            var rows = await query
                .OrderByDescending(r => r.StartedAtUtc)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .ToListAsync(cancellationToken);

            var items = rows.Select(MapRunToDto).ToList();
            return Result<PagedResult<JobImportRunDto>>.Success(new PagedResult<JobImportRunDto>(items, page, pageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching job import runs");
            return Result<PagedResult<JobImportRunDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading import runs.");
        }
    }

    private static JobImportRunDto MapRunToDto(JobImportRun r) => new()
    {
        Id = r.Id,
        SourceId = r.SourceId,
        SourceName = r.SourceName,
        Status = r.Status,
        StatusLabel = r.Status.ToString(),
        Trigger = r.Trigger,
        TriggerLabel = r.Trigger.ToString(),
        TriggeredByUserId = r.TriggeredByUserId,
        StartedAtUtc = r.StartedAtUtc,
        CompletedAtUtc = r.CompletedAtUtc,
        DurationMs = r.DurationMs,
        JobsFound = r.JobsFound,
        JobsCreated = r.JobsCreated,
        JobsUpdated = r.JobsUpdated,
        JobsSkipped = r.JobsSkipped,
        JobsExpired = r.JobsExpired,
        IsSuccess = r.IsSuccess,
        FailureMessage = r.FailureMessage,
        Notes = r.Notes,
        HttpStatusCode = r.HttpStatusCode
    };
}

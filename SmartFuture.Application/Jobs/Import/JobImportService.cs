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

    private readonly IAppDbContext _dbContext;
    private readonly IJobSourceFetcher _fetcher;
    private readonly IJobContentExtractor _extractor;
    private readonly IJobOpportunityService _jobService;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<JobImportService> _logger;

    public JobImportService(IAppDbContext dbContext, IJobSourceFetcher fetcher, IJobContentExtractor extractor, IJobOpportunityService jobService,
        IAuditService auditService, ICurrentUserService currentUser, ILogger<JobImportService> logger)
    {
        _dbContext = dbContext;
        _fetcher = fetcher;
        _extractor = extractor;
        _jobService = jobService;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
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
            return Result<JobImportSummaryDto>.Failure(ErrorCodes.EXCEPTION, "The import run was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error running job import for all sources");
            return Result<JobImportSummaryDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while running the job import.");
        }
    }

    // ─── One source, fully guarded ────────────────────────────────────

    private async Task<JobImportRun> ExecuteSourceRunAsync(JobSource source, JobImportRunTrigger trigger, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
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
        source.LastCheckedAtUtc = run.StartedAtUtc;
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

            var extraction = _extractor.Extract(source, fetch.Content ?? string.Empty, fetch.ContentType, DateTime.UtcNow);
            notes.AddRange(extraction.Notes);
            run.JobsFound = extraction.Jobs.Count;

            if (extraction.Jobs.Count == 0)
            {
                // Not a crash — some pages genuinely have nothing today,
                // and some block us with a 200-status interstitial. Both
                // are recorded as a failed run so the source's health
                // reflects that it is not producing.
                await FinishRunAsync(run, source, stopwatch, success: false,
                    "The page was fetched but no job listings could be extracted from it.", notes, cancellationToken);
                return run;
            }

            var capped = extraction.Jobs.Take(Math.Max(1, source.MaxJobsPerRun)).ToList();
            if (extraction.Jobs.Count > capped.Count)
                notes.Add($"Source returned {extraction.Jobs.Count} jobs; capped at MaxJobsPerRun={source.MaxJobsPerRun}.");

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

            notes.Add($"Extraction strategy: {extraction.StrategyUsed}.");
            await FinishRunAsync(run, source, stopwatch, success: true, null, notes, cancellationToken);
            return run;
        }
        catch (OperationCanceledException)
        {
            await FinishRunAsync(run, source, stopwatch, success: false, "The import run was cancelled.", notes, CancellationToken.None);
            return run;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error importing job source {SourceId}", source.Id);
            await FinishRunAsync(run, source, stopwatch, success: false, $"Unexpected error: {ex.Message}", notes, CancellationToken.None);
            return run;
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
        entity.Location = location ?? entity.Location;
        entity.City = city ?? candidate.City ?? entity.City;
        entity.Province = province ?? candidate.Province ?? entity.Province;
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

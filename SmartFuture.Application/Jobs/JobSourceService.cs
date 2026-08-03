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

namespace SmartFuture.Application.Jobs;

// Admin CRUD for crawl sources. Health fields (LastChecked/Success/
// Failure) are written by the importer, never by this service — that
// keeps "what the admin configured" and "what the crawler observed"
// cleanly separated.
public class JobSourceService : IJobSourceService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MinCrawlFrequencyMinutes = 15;
    private const int MaxCrawlFrequencyMinutes = 10080;
    private const int MaxJobsPerRunCeiling = 500;
    // Paging is bounded in practice by MaxJobsPerRun — the crawl stops
    // as soon as enough job links are queued — so the real request
    // budget is roughly MaxJobsPerRun + pages walked. 50 allows a
    // deliberate full-archive import without letting a typo turn into
    // an unbounded crawl.
    private const int MaxPagesPerRunCeiling = 50;

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<JobSourceService> _logger;

    public JobSourceService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, ILogger<JobSourceService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<PagedResult<JobSourceDto>>> SearchAsync(JobSourceFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new JobSourceFilterRequestDto();
            var page = Math.Max(1, filter.Page ?? 1);
            var pageSize = Math.Clamp(filter.PageSize ?? DefaultPageSize, 1, MaxPageSize);

            var query = _dbContext.JobSources.AsNoTracking().AsQueryable();

            if (filter.IsActive.HasValue)
                query = query.Where(s => s.IsActive == filter.IsActive.Value);

            if (filter.SourceType.HasValue)
                query = query.Where(s => s.SourceType == filter.SourceType.Value);

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var s = filter.Search.Trim().ToLower();
                query = query.Where(x => x.SourceName.ToLower().Contains(s) || x.SourceUrl.ToLower().Contains(s));
            }

            var totalCount = await query.CountAsync(cancellationToken);

            // Project the per-source job counts in the same round-trip so
            // the admin list doesn't need N+1 follow-up queries.
            var rows = await query
                .OrderByDescending(s => s.IsActive).ThenBy(s => s.SourceName)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(s => new
                {
                    Source = s,
                    ActiveJobCount = _dbContext.JobOpportunities.Count(j => j.SourceId == s.Id && j.Status == JobOpportunityStatus.Active),
                    TotalJobCount = _dbContext.JobOpportunities.Count(j => j.SourceId == s.Id)
                })
                .ToListAsync(cancellationToken);

            var items = rows.Select(r => MapToDto(r.Source, r.ActiveJobCount, r.TotalJobCount)).ToList();
            return Result<PagedResult<JobSourceDto>>.Success(new PagedResult<JobSourceDto>(items, page, pageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching job sources");
            return Result<PagedResult<JobSourceDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading job sources.");
        }
    }

    public async Task<Result<JobSourceDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<JobSourceDto>.Failure(ErrorCodes.BAD_REQUEST, "Source id is required.");

            var entity = await _dbContext.JobSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (entity is null)
                return Result<JobSourceDto>.Failure(ErrorCodes.NOT_FOUND, "Job source not found.");

            var activeCount = await _dbContext.JobOpportunities.CountAsync(j => j.SourceId == id && j.Status == JobOpportunityStatus.Active, cancellationToken);
            var totalCount = await _dbContext.JobOpportunities.CountAsync(j => j.SourceId == id, cancellationToken);

            return Result<JobSourceDto>.Success(MapToDto(entity, activeCount, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching job source {Id}", id);
            return Result<JobSourceDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading the job source.");
        }
    }

    public async Task<Result<JobSourceDto>> CreateAsync(CreateJobSourceRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var validation = ValidateMutation(request?.SourceName, request?.SourceUrl, request?.CrawlFrequencyMinutes, request?.MaxJobsPerRun,
                requireUrl: true, maxPages: request?.MaxPagesPerRun);
            if (validation is not null) return validation;

            var url = request!.SourceUrl.Trim();
            var clash = await _dbContext.JobSources.AsNoTracking().AnyAsync(s => s.SourceUrl == url, cancellationToken);
            if (clash)
                return Result<JobSourceDto>.Failure(ErrorCodes.CONFLICT, "A job source with that URL already exists.");

            var entity = new JobSource
            {
                SourceName = request.SourceName.Trim(),
                SourceUrl = url,
                SourceType = request.SourceType,
                IsActive = request.IsActive ?? true,
                Notes = JobTextUtilities.NullIfBlank(request.Notes),
                DefaultCategory = JobTextUtilities.NullIfBlank(request.DefaultCategory),
                DefaultLocation = JobTextUtilities.NullIfBlank(request.DefaultLocation),
                CrawlFrequencyMinutes = request.CrawlFrequencyMinutes,
                AutoPublish = request.AutoPublish ?? false,
                MaxJobsPerRun = request.MaxJobsPerRun ?? 20,
                MaxPagesPerRun = request.MaxPagesPerRun ?? 1
            };

            _dbContext.JobSources.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await LogSourceAuditAsync(AuditActionType.JobSourceCreated, entity, $"Job source created: {entity.SourceName} ({entity.SourceUrl})");
            return Result<JobSourceDto>.Success(MapToDto(entity, 0, 0), "Job source created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating job source");
            return Result<JobSourceDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the job source.");
        }
    }

    public async Task<Result<JobSourceDto>> UpdateAsync(Guid id, UpdateJobSourceRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<JobSourceDto>.Failure(ErrorCodes.BAD_REQUEST, "Source id is required.");
            if (request is null)
                return Result<JobSourceDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateMutation(request.SourceName, request.SourceUrl, request.CrawlFrequencyMinutes, request.MaxJobsPerRun,
                requireUrl: false, maxPages: request.MaxPagesPerRun);
            if (validation is not null) return validation;

            var entity = await _dbContext.JobSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (entity is null)
                return Result<JobSourceDto>.Failure(ErrorCodes.NOT_FOUND, "Job source not found.");

            if (!string.IsNullOrWhiteSpace(request.SourceUrl))
            {
                var url = request.SourceUrl.Trim();
                if (!string.Equals(url, entity.SourceUrl, StringComparison.OrdinalIgnoreCase))
                {
                    var clash = await _dbContext.JobSources.AsNoTracking().AnyAsync(s => s.SourceUrl == url && s.Id != id, cancellationToken);
                    if (clash)
                        return Result<JobSourceDto>.Failure(ErrorCodes.CONFLICT, "Another job source already uses that URL.");
                    entity.SourceUrl = url;
                }
            }

            if (!string.IsNullOrWhiteSpace(request.SourceName)) entity.SourceName = request.SourceName.Trim();
            if (request.SourceType.HasValue) entity.SourceType = request.SourceType.Value;
            if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;
            if (request.Notes is not null) entity.Notes = JobTextUtilities.NullIfBlank(request.Notes);
            if (request.DefaultCategory is not null) entity.DefaultCategory = JobTextUtilities.NullIfBlank(request.DefaultCategory);
            if (request.DefaultLocation is not null) entity.DefaultLocation = JobTextUtilities.NullIfBlank(request.DefaultLocation);
            if (request.CrawlFrequencyMinutes.HasValue) entity.CrawlFrequencyMinutes = request.CrawlFrequencyMinutes.Value;
            if (request.AutoPublish.HasValue) entity.AutoPublish = request.AutoPublish.Value;
            if (request.MaxJobsPerRun.HasValue) entity.MaxJobsPerRun = request.MaxJobsPerRun.Value;
            if (request.MaxPagesPerRun.HasValue) entity.MaxPagesPerRun = request.MaxPagesPerRun.Value;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await LogSourceAuditAsync(AuditActionType.JobSourceUpdated, entity, $"Job source updated: {entity.SourceName}");

            var activeCount = await _dbContext.JobOpportunities.CountAsync(j => j.SourceId == id && j.Status == JobOpportunityStatus.Active, cancellationToken);
            var totalCount = await _dbContext.JobOpportunities.CountAsync(j => j.SourceId == id, cancellationToken);
            return Result<JobSourceDto>.Success(MapToDto(entity, activeCount, totalCount), "Job source updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating job source {Id}", id);
            return Result<JobSourceDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the job source.");
        }
    }

    public async Task<Result<JobSourceDto>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
        => await UpdateAsync(id, new UpdateJobSourceRequestDto { IsActive = isActive }, cancellationToken);

    // ─── Permanent delete ─────────────────────────────────────────────

    public async Task<Result<JobSourceDeletePreviewDto>> GetDeletePreviewAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<JobSourceDeletePreviewDto>.Failure(ErrorCodes.BAD_REQUEST, "Source id is required.");

            var source = await _dbContext.JobSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (source is null)
                return Result<JobSourceDeletePreviewDto>.Failure(ErrorCodes.NOT_FOUND, "Job source not found.");

            var jobs = _dbContext.JobOpportunities.AsNoTracking().Where(j => j.SourceId == id);

            var preview = new JobSourceDeletePreviewDto
            {
                SourceId = source.Id,
                SourceName = source.SourceName,
                SourceUrl = source.SourceUrl,
                ActiveJobs = await jobs.CountAsync(j => j.Status == JobOpportunityStatus.Active, cancellationToken),
                DraftJobs = await jobs.CountAsync(j => j.Status == JobOpportunityStatus.Draft, cancellationToken),
                HiddenJobs = await jobs.CountAsync(j => j.Status == JobOpportunityStatus.Hidden, cancellationToken),
                ExpiredJobs = await jobs.CountAsync(j => j.Status == JobOpportunityStatus.Expired, cancellationToken),
                DeletedJobs = await jobs.CountAsync(j => j.Status == JobOpportunityStatus.Deleted, cancellationToken),
                ManuallyEditedJobs = await jobs.CountAsync(j => j.IsManuallyEdited, cancellationToken),
                ImportRuns = await _dbContext.JobImportRuns.AsNoTracking().CountAsync(r => r.SourceId == id, cancellationToken),
                // Import runs are history and always survive.
                WillDeleteImportRuns = false
            };

            preview.WillDeleteJobs = await jobs.CountAsync(j => !j.IsManuallyEdited, cancellationToken);
            preview.WillPreserveManualJobs = preview.ManuallyEditedJobs;

            return Result<JobSourceDeletePreviewDto>.Success(preview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building delete preview for job source {Id}", id);
            return Result<JobSourceDeletePreviewDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while building the delete preview.");
        }
    }

    public async Task<Result<JobSourceDeleteResultDto>> DeleteAsync(Guid id, bool deleteManuallyEditedJobs, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<JobSourceDeleteResultDto>.Failure(ErrorCodes.BAD_REQUEST, "Source id is required.");

            var source = await _dbContext.JobSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (source is null)
                return Result<JobSourceDeleteResultDto>.Failure(ErrorCodes.NOT_FOUND, "Job source not found.");

            // Scoped to THIS source only — every query below filters on
            // SourceId, so no other source's jobs can be touched.
            var jobs = await _dbContext.JobOpportunities.Where(j => j.SourceId == id).ToListAsync(cancellationToken);
            var doomed = deleteManuallyEditedJobs ? jobs : jobs.Where(j => !j.IsManuallyEdited).ToList();
            var preserved = jobs.Except(doomed).ToList();

            _dbContext.JobOpportunities.RemoveRange(doomed);

            // Detach survivors explicitly rather than relying on the
            // database's ON DELETE SET NULL. Same end state, but it is
            // deterministic across providers and visible in the code.
            // SourceName and SourceUrl are snapshots on the job itself,
            // so provenance is not lost.
            foreach (var job in preserved) job.SourceId = null;

            // Import runs are audit history and outlive the source. The
            // FK is nullable with a SourceName snapshot on the run, so
            // detaching keeps the log readable.
            var runs = await _dbContext.JobImportRuns.Where(r => r.SourceId == id).ToListAsync(cancellationToken);
            foreach (var run in runs) run.SourceId = null;

            _dbContext.JobSources.Remove(source);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await LogSourceAuditAsync(AuditActionType.JobSourceDeleted, source,
                $"Job source permanently deleted: {source.SourceName} ({source.SourceUrl}). "
                + $"{doomed.Count} job(s) deleted, {preserved.Count} manually-edited job(s) preserved, {runs.Count} import run(s) kept as history.");

            _logger.LogInformation("[JobSource] permanently deleted {Source}: {Deleted} jobs removed, {Preserved} preserved, {Runs} runs detached",
                source.SourceName, doomed.Count, preserved.Count, runs.Count);

            return Result<JobSourceDeleteResultDto>.Success(new JobSourceDeleteResultDto
            {
                SourceDeleted = true,
                SourceName = source.SourceName,
                JobsDeleted = doomed.Count,
                ManualJobsPreserved = preserved.Count,
                ImportRunsPreserved = runs.Count,
                ImportRunsDeleted = 0
            }, $"Deleted '{source.SourceName}'. {doomed.Count} job(s) removed"
                + (preserved.Count > 0 ? $", {preserved.Count} manually-edited job(s) preserved." : "."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting job source {Id}", id);
            return Result<JobSourceDeleteResultDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while deleting the job source.");
        }
    }

    private static Result<JobSourceDto>? ValidateMutation(string? name, string? url, int? crawlFrequency, int? maxJobs, bool requireUrl, int? maxPages = null)
    {
        if (requireUrl && string.IsNullOrWhiteSpace(name))
            return Result<JobSourceDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Source name is required.");

        if (requireUrl && string.IsNullOrWhiteSpace(url))
            return Result<JobSourceDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Source URL is required.");

        if (!string.IsNullOrWhiteSpace(url))
        {
            // Only http/https are fetchable; rejecting anything else here
            // stops a file:// or ftp:// entry reaching the crawler.
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return Result<JobSourceDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Source URL must be a valid http(s) address.");
            }
        }

        if (crawlFrequency.HasValue && (crawlFrequency < MinCrawlFrequencyMinutes || crawlFrequency > MaxCrawlFrequencyMinutes))
            return Result<JobSourceDto>.Failure(ErrorCodes.VALIDATION_ERROR, $"Crawl frequency must be between {MinCrawlFrequencyMinutes} minutes and 7 days.");

        if (maxJobs.HasValue && (maxJobs < 1 || maxJobs > MaxJobsPerRunCeiling))
            return Result<JobSourceDto>.Failure(ErrorCodes.VALIDATION_ERROR, $"Max jobs per run must be between 1 and {MaxJobsPerRunCeiling}.");

        if (maxPages.HasValue && (maxPages < 1 || maxPages > MaxPagesPerRunCeiling))
            return Result<JobSourceDto>.Failure(ErrorCodes.VALIDATION_ERROR, $"Max pages per run must be between 1 and {MaxPagesPerRunCeiling}.");

        return null;
    }

    private async Task LogSourceAuditAsync(AuditActionType action, JobSource entity, string summary)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = _currentUser.UserId.HasValue ? AuditActorType.User : AuditActorType.System,
            ActionType = action,
            EntityType = AuditEntityType.JobSource,
            EntityId = entity.Id,
            EntityName = entity.SourceName,
            Summary = summary,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private static JobSourceDto MapToDto(JobSource s, int activeJobCount, int totalJobCount) => new()
    {
        Id = s.Id,
        SourceName = s.SourceName,
        SourceUrl = s.SourceUrl,
        SourceType = s.SourceType,
        SourceTypeLabel = s.SourceType.ToString(),
        IsActive = s.IsActive,
        Notes = s.Notes,
        DefaultCategory = s.DefaultCategory,
        DefaultLocation = s.DefaultLocation,
        CrawlFrequencyMinutes = s.CrawlFrequencyMinutes,
        AutoPublish = s.AutoPublish,
        MaxJobsPerRun = s.MaxJobsPerRun,
        MaxPagesPerRun = s.MaxPagesPerRun,
        LastCheckedAtUtc = s.LastCheckedAtUtc,
        LastSuccessAtUtc = s.LastSuccessAtUtc,
        LastFailureAtUtc = s.LastFailureAtUtc,
        LastFailureMessage = s.LastFailureMessage,
        ConsecutiveFailureCount = s.ConsecutiveFailureCount,
        TotalJobsImported = s.TotalJobsImported,
        ActiveJobCount = activeJobCount,
        TotalJobCount = totalJobCount,
        HealthLabel = ResolveHealthLabel(s),
        CreatedAtUtc = s.CreatedAtUtc,
        UpdatedAtUtc = s.UpdatedAtUtc
    };

    // Drives the status pill on the admin Sources page.
    private static string ResolveHealthLabel(JobSource s)
    {
        if (!s.IsActive) return "Disabled";
        if (s.SourceType == JobSourceType.Manual) return "Manual";
        if (s.LastCheckedAtUtc is null) return "Never run";
        if (s.ConsecutiveFailureCount >= 3) return "Failing";
        if (s.ConsecutiveFailureCount > 0) return "Degraded";
        return "Healthy";
    }
}

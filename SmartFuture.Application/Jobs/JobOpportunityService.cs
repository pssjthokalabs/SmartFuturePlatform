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

// Read/write surface for job listings — public search + detail, and the
// admin moderation actions.
//
// The public visibility rule lives in ONE place (VisibleToPublic) so a
// hidden/expired job can never leak through a facet count, a search
// result, or the detail endpoint.
public class JobOpportunityService : IJobOpportunityService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    // A listing inside this window gets the "closing soon" flag.
    private const int ClosingSoonDays = 7;

    private readonly IAppDbContext _dbContext;
    private readonly IJobSettingsService _settingsService;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<JobOpportunityService> _logger;

    public JobOpportunityService(IAppDbContext dbContext, IJobSettingsService settingsService, IAuditService auditService, ICurrentUserService currentUser,
        ILogger<JobOpportunityService> logger)
    {
        _dbContext = dbContext;
        _settingsService = settingsService;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    // THE public visibility rule. Active only, and not past its closing
    // date. A job with no closing date stays listed until an admin
    // hides/expires it or the source stops carrying it.
    private static IQueryable<JobOpportunity> VisibleToPublic(IQueryable<JobOpportunity> query, DateTime nowUtc)
        => query.Where(j => j.Status == JobOpportunityStatus.Active
            && (j.ClosingDateUtc == null || j.ClosingDateUtc >= nowUtc));

    // ─── Public search ────────────────────────────────────────────────

    public async Task<Result<PagedResult<JobOpportunityDto>>> SearchPublicAsync(JobOpportunityFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new JobOpportunityFilterRequestDto();

            var settings = await _settingsService.GetOrCreateAsync(cancellationToken);
            if (!settings.JobsModuleEnabled)
            {
                // Module switched off — an empty page is the honest
                // answer, and it keeps the website rendering instead of
                // erroring.
                return Result<PagedResult<JobOpportunityDto>>.Success(
                    new PagedResult<JobOpportunityDto>(Array.Empty<JobOpportunityDto>(), 1, DefaultPageSize, 0),
                    "Job opportunities are currently unavailable.");
            }

            var now = DateTime.UtcNow;
            var page = Math.Max(1, filter.Page ?? 1);
            var pageSize = Math.Clamp(filter.PageSize ?? DefaultPageSize, 1, MaxPageSize);

            var query = VisibleToPublic(_dbContext.JobOpportunities.AsNoTracking(), now);
            query = ApplyPublicFilters(query, filter);
            query = ApplySort(query, filter.Sort);

            var totalCount = await query.CountAsync(cancellationToken);
            var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

            // List projection: never carries the gated long-form fields,
            // regardless of who is asking.
            var items = rows.Select(j => MapToPublicDto(j, now, includeDetail: false, requiresSubscription: false)).ToList();
            return Result<PagedResult<JobOpportunityDto>>.Success(new PagedResult<JobOpportunityDto>(items, page, pageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching job opportunities (public)");
            return Result<PagedResult<JobOpportunityDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while searching jobs.");
        }
    }

    public async Task<Result<JobOpportunityDto>> GetPublicBySlugOrIdAsync(string slugOrId, bool callerIsSubscriber, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(slugOrId))
                return Result<JobOpportunityDto>.Failure(ErrorCodes.BAD_REQUEST, "A job slug or id is required.");

            var settings = await _settingsService.GetOrCreateAsync(cancellationToken);
            if (!settings.JobsModuleEnabled)
                return Result<JobOpportunityDto>.Failure(ErrorCodes.NOT_FOUND, "Job opportunities are currently unavailable.");

            var now = DateTime.UtcNow;
            var key = slugOrId.Trim();

            // Accept either the slug or the raw Guid so a link that was
            // shared before a slug rename still resolves.
            var query = VisibleToPublic(_dbContext.JobOpportunities.AsNoTracking(), now);
            JobOpportunity? entity;
            if (Guid.TryParse(key, out var id))
                entity = await query.FirstOrDefaultAsync(j => j.Id == id || j.Slug == key, cancellationToken);
            else
                entity = await query.FirstOrDefaultAsync(j => j.Slug == key, cancellationToken);

            if (entity is null)
                return Result<JobOpportunityDto>.Failure(ErrorCodes.NOT_FOUND, "That job opportunity is no longer available.");

            // Subscriber gate. We still return the card-level payload so
            // the page can render a teaser + a subscribe call to action;
            // only the long-form/apply fields are withheld.
            var gated = settings.JobDetailsSubscribersOnly && !callerIsSubscriber;
            var dto = MapToPublicDto(entity, now, includeDetail: !gated, requiresSubscription: gated);

            // Fire-and-forget view counter. A failure here must never
            // break the page, so it is deliberately not awaited into the
            // result path.
            await TryIncrementViewCountAsync(entity.Id, cancellationToken);

            return Result<JobOpportunityDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching job opportunity {Key}", slugOrId);
            return Result<JobOpportunityDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading the job.");
        }
    }

    private async Task TryIncrementViewCountAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.JobOpportunities
                .Where(j => j.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ViewCount, j => j.ViewCount + 1), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "View-count increment failed for job {JobId}", id);
        }
    }

    // ─── Facets ───────────────────────────────────────────────────────
    //
    // Counts use the SAME visibility rule as the list, so a chip can
    // never advertise jobs the list won't return.

    public async Task<Result<IReadOnlyList<JobCategoryDto>>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var rows = await VisibleToPublic(_dbContext.JobOpportunities.AsNoTracking(), now)
                .Where(j => j.Category != null && j.Category != "")
                .GroupBy(j => j.Category!)
                .Select(g => new JobCategoryDto { Category = g.Key, JobCount = g.Count() })
                .OrderByDescending(c => c.JobCount).ThenBy(c => c.Category)
                .ToListAsync(cancellationToken);

            return Result<IReadOnlyList<JobCategoryDto>>.Success(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading job categories");
            return Result<IReadOnlyList<JobCategoryDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading categories.");
        }
    }

    public async Task<Result<IReadOnlyList<JobLocationDto>>> GetLocationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var visible = VisibleToPublic(_dbContext.JobOpportunities.AsNoTracking(), now);

            // Prefer City (what a seeker actually filters by) and fall
            // back to the free-text Location when no city was parsed.
            var cityRows = await visible
                .Where(j => j.City != null && j.City != "")
                .GroupBy(j => new { City = j.City!, j.Province })
                .Select(g => new JobLocationDto { Location = g.Key.City, Province = g.Key.Province, JobCount = g.Count() })
                .ToListAsync(cancellationToken);

            var looseRows = await visible
                .Where(j => (j.City == null || j.City == "") && j.Location != null && j.Location != "")
                .GroupBy(j => j.Location!)
                .Select(g => new JobLocationDto { Location = g.Key, JobCount = g.Count() })
                .ToListAsync(cancellationToken);

            // Merge case-insensitively so "Cape Town" and "cape town"
            // land on one chip.
            var merged = cityRows.Concat(looseRows)
                .GroupBy(r => r.Location, StringComparer.OrdinalIgnoreCase)
                .Select(g => new JobLocationDto
                {
                    Location = g.First().Location,
                    Province = g.Select(x => x.Province).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                    JobCount = g.Sum(x => x.JobCount)
                })
                .OrderByDescending(r => r.JobCount).ThenBy(r => r.Location)
                .ToList();

            return Result<IReadOnlyList<JobLocationDto>>.Success(merged);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading job locations");
            return Result<IReadOnlyList<JobLocationDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading locations.");
        }
    }

    public async Task<Result<IReadOnlyList<JobSourceFacetDto>>> GetSourceFacetsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var rows = await VisibleToPublic(_dbContext.JobOpportunities.AsNoTracking(), now)
                .GroupBy(j => j.SourceName)
                .Select(g => new JobSourceFacetDto { SourceName = g.Key, JobCount = g.Count() })
                .OrderByDescending(s => s.JobCount).ThenBy(s => s.SourceName)
                .ToListAsync(cancellationToken);

            return Result<IReadOnlyList<JobSourceFacetDto>>.Success(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading job source facets");
            return Result<IReadOnlyList<JobSourceFacetDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading sources.");
        }
    }

    // ─── Admin search / detail ────────────────────────────────────────

    public async Task<Result<PagedResult<AdminJobOpportunityDto>>> SearchAdminAsync(AdminJobOpportunityFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminJobOpportunityFilterRequestDto();

            var now = DateTime.UtcNow;
            var page = Math.Max(1, filter.Page ?? 1);
            var pageSize = Math.Clamp(filter.PageSize ?? DefaultPageSize, 1, MaxPageSize);

            var query = _dbContext.JobOpportunities.AsNoTracking().AsQueryable();

            // Soft-deleted rows exist only for de-duplication history —
            // they're hidden unless explicitly requested.
            if (filter.Status.HasValue)
                query = query.Where(j => j.Status == filter.Status.Value);
            else
                query = query.Where(j => j.Status != JobOpportunityStatus.Deleted);

            if (filter.SourceId.HasValue)
                query = query.Where(j => j.SourceId == filter.SourceId.Value);

            if (!string.IsNullOrWhiteSpace(filter.SourceName))
            {
                var sourceName = filter.SourceName.Trim();
                query = query.Where(j => j.SourceName == sourceName);
            }

            if (!string.IsNullOrWhiteSpace(filter.Category))
            {
                var category = filter.Category.Trim();
                query = query.Where(j => j.Category == category);
            }

            if (!string.IsNullOrWhiteSpace(filter.Location))
            {
                var location = filter.Location.Trim().ToLower();
                query = query.Where(j =>
                    (j.Location != null && j.Location.ToLower().Contains(location))
                    || (j.City != null && j.City.ToLower().Contains(location))
                    || (j.Province != null && j.Province.ToLower().Contains(location)));
            }

            if (filter.WorkplaceType.HasValue)
                query = query.Where(j => j.WorkplaceType == filter.WorkplaceType.Value);

            if (filter.IsFeatured.HasValue)
                query = query.Where(j => j.IsFeatured == filter.IsFeatured.Value);

            if (filter.IsExpired.HasValue)
            {
                query = filter.IsExpired.Value
                    ? query.Where(j => j.ClosingDateUtc != null && j.ClosingDateUtc < now)
                    : query.Where(j => j.ClosingDateUtc == null || j.ClosingDateUtc >= now);
            }

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var s = filter.Search.Trim().ToLower();
                query = query.Where(j =>
                    j.Title.ToLower().Contains(s)
                    || (j.CompanyName != null && j.CompanyName.ToLower().Contains(s))
                    || (j.Location != null && j.Location.ToLower().Contains(s))
                    || (j.Category != null && j.Category.ToLower().Contains(s))
                    || j.SourceName.ToLower().Contains(s));
            }

            query = ApplySort(query, filter.Sort);

            var totalCount = await query.CountAsync(cancellationToken);
            var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

            var items = rows.Select(j => MapToAdminDto(j, now)).ToList();
            return Result<PagedResult<AdminJobOpportunityDto>>.Success(new PagedResult<AdminJobOpportunityDto>(items, page, pageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching job opportunities (admin)");
            return Result<PagedResult<AdminJobOpportunityDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while searching jobs.");
        }
    }

    public async Task<Result<AdminJobOpportunityDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.BAD_REQUEST, "Job id is required.");

            var entity = await _dbContext.JobOpportunities.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
            return entity is null
                ? Result<AdminJobOpportunityDto>.Failure(ErrorCodes.NOT_FOUND, "Job opportunity not found.")
                : Result<AdminJobOpportunityDto>.Success(MapToAdminDto(entity, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching job opportunity {Id}", id);
            return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading the job.");
        }
    }

    // ─── Admin mutations ──────────────────────────────────────────────

    public async Task<Result<AdminJobOpportunityDto>> CreateAsync(CreateJobOpportunityRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Title))
                return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Job title is required.");

            var sourceName = JobTextUtilities.NullIfBlank(request.SourceName) ?? "Manual";
            if (request.SourceId.HasValue)
            {
                var source = await _dbContext.JobSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.SourceId.Value, cancellationToken);
                if (source is null)
                    return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.VALIDATION_ERROR, "The selected job source does not exist.");
                sourceName = source.SourceName;
            }

            var fingerprint = JobTextUtilities.ComputeFingerprint(request.SourceUrl, request.Title, request.CompanyName, request.Location);
            var duplicate = await _dbContext.JobOpportunities.AsNoTracking()
                .FirstOrDefaultAsync(j => j.Fingerprint == fingerprint, cancellationToken);
            if (duplicate is not null)
            {
                return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.CONFLICT,
                    $"An identical job already exists ({duplicate.Title}). Edit that listing instead of creating a duplicate.");
            }

            var descriptionText = JobTextUtilities.NullIfBlank(request.DescriptionText);
            var entity = new JobOpportunity
            {
                Title = request.Title.Trim(),
                Slug = await BuildUniqueSlugAsync(request.Title, request.CompanyName, cancellationToken),
                CompanyName = JobTextUtilities.NullIfBlank(request.CompanyName),
                Location = JobTextUtilities.NullIfBlank(request.Location),
                Country = JobTextUtilities.NullIfBlank(request.Country) ?? "South Africa",
                Province = JobTextUtilities.NullIfBlank(request.Province),
                City = JobTextUtilities.NullIfBlank(request.City),
                Category = JobTextUtilities.NullIfBlank(request.Category),
                WorkplaceType = request.WorkplaceType ?? JobWorkplaceType.Unknown,
                EmploymentType = JobTextUtilities.NullIfBlank(request.EmploymentType),
                SalaryText = JobTextUtilities.NullIfBlank(request.SalaryText),
                Summary = JobTextUtilities.NullIfBlank(request.Summary) ?? JobTextUtilities.BuildSummary(descriptionText),
                DescriptionHtml = JobTextUtilities.NullIfBlank(request.DescriptionHtml),
                DescriptionText = descriptionText,
                RequirementsText = JobTextUtilities.NullIfBlank(request.RequirementsText),
                ApplicationInstructions = JobTextUtilities.NullIfBlank(request.ApplicationInstructions),
                ApplyUrl = JobTextUtilities.NullIfBlank(request.ApplyUrl),
                ApplyEmail = JobTextUtilities.NullIfBlank(request.ApplyEmail),
                SourceUrl = JobTextUtilities.NullIfBlank(request.SourceUrl),
                SourceName = sourceName,
                SourceId = request.SourceId,
                Fingerprint = fingerprint,
                PostedDateUtc = request.PostedDateUtc,
                ClosingDateUtc = request.ClosingDateUtc,
                ImportedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                // A manually created job is manually edited by definition
                // — the importer must never overwrite it.
                IsManuallyEdited = true,
                IsFeatured = request.IsFeatured ?? false,
                LogoUrl = JobTextUtilities.NullIfBlank(request.LogoUrl),
                TagsJson = JobTextUtilities.WriteStringList(request.Tags),
                Status = request.PublishImmediately ? JobOpportunityStatus.Active : JobOpportunityStatus.Draft
            };

            _dbContext.JobOpportunities.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await LogJobAuditAsync(AuditActionType.JobOpportunityCreated, entity, $"Job created manually: {entity.Title}");
            return Result<AdminJobOpportunityDto>.Success(MapToAdminDto(entity, DateTime.UtcNow), "Job opportunity created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating job opportunity");
            return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the job.");
        }
    }

    public async Task<Result<AdminJobOpportunityDto>> UpdateAsync(Guid id, UpdateJobOpportunityRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.BAD_REQUEST, "Job id is required.");
            if (request is null)
                return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var entity = await _dbContext.JobOpportunities.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
            if (entity is null)
                return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.NOT_FOUND, "Job opportunity not found.");

            if (request.Title is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Title))
                    return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Job title cannot be empty.");
                entity.Title = request.Title.Trim();
            }

            if (request.CompanyName is not null) entity.CompanyName = JobTextUtilities.NullIfBlank(request.CompanyName);
            if (request.Location is not null) entity.Location = JobTextUtilities.NullIfBlank(request.Location);
            if (request.Country is not null) entity.Country = JobTextUtilities.NullIfBlank(request.Country);
            if (request.Province is not null) entity.Province = JobTextUtilities.NullIfBlank(request.Province);
            if (request.City is not null) entity.City = JobTextUtilities.NullIfBlank(request.City);
            if (request.Category is not null) entity.Category = JobTextUtilities.NullIfBlank(request.Category);
            if (request.WorkplaceType.HasValue) entity.WorkplaceType = request.WorkplaceType.Value;
            if (request.EmploymentType is not null) entity.EmploymentType = JobTextUtilities.NullIfBlank(request.EmploymentType);
            if (request.SalaryText is not null) entity.SalaryText = JobTextUtilities.NullIfBlank(request.SalaryText);
            if (request.Summary is not null) entity.Summary = JobTextUtilities.NullIfBlank(request.Summary);
            if (request.DescriptionHtml is not null) entity.DescriptionHtml = JobTextUtilities.NullIfBlank(request.DescriptionHtml);
            if (request.DescriptionText is not null) entity.DescriptionText = JobTextUtilities.NullIfBlank(request.DescriptionText);
            if (request.RequirementsText is not null) entity.RequirementsText = JobTextUtilities.NullIfBlank(request.RequirementsText);
            if (request.ApplicationInstructions is not null) entity.ApplicationInstructions = JobTextUtilities.NullIfBlank(request.ApplicationInstructions);
            if (request.ApplyUrl is not null) entity.ApplyUrl = JobTextUtilities.NullIfBlank(request.ApplyUrl);
            if (request.ApplyEmail is not null) entity.ApplyEmail = JobTextUtilities.NullIfBlank(request.ApplyEmail);
            if (request.PostedDateUtc.HasValue) entity.PostedDateUtc = request.PostedDateUtc;
            if (request.ClosingDateUtc.HasValue) entity.ClosingDateUtc = request.ClosingDateUtc;
            if (request.IsFeatured.HasValue) entity.IsFeatured = request.IsFeatured.Value;
            if (request.LogoUrl is not null) entity.LogoUrl = JobTextUtilities.NullIfBlank(request.LogoUrl);
            if (request.Tags is not null) entity.TagsJson = JobTextUtilities.WriteStringList(request.Tags);

            // Summary is what the card shows — keep it populated when the
            // admin clears it but leaves a description behind.
            entity.Summary ??= JobTextUtilities.BuildSummary(entity.DescriptionText);

            // From here on, re-crawls only refresh LastSeenAtUtc: the
            // admin's corrections outrank whatever the source says.
            entity.IsManuallyEdited = true;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await LogJobAuditAsync(AuditActionType.JobOpportunityUpdated, entity, $"Job updated: {entity.Title}");
            return Result<AdminJobOpportunityDto>.Success(MapToAdminDto(entity, DateTime.UtcNow), "Job opportunity updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating job opportunity {Id}", id);
            return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the job.");
        }
    }

    public Task<Result<AdminJobOpportunityDto>> PublishAsync(Guid id, CancellationToken cancellationToken = default)
        => TransitionStatusAsync(id, JobOpportunityStatus.Active, "published", cancellationToken);

    public Task<Result<AdminJobOpportunityDto>> HideAsync(Guid id, CancellationToken cancellationToken = default)
        => TransitionStatusAsync(id, JobOpportunityStatus.Hidden, "hidden", cancellationToken);

    public Task<Result<AdminJobOpportunityDto>> ExpireAsync(Guid id, CancellationToken cancellationToken = default)
        => TransitionStatusAsync(id, JobOpportunityStatus.Expired, "expired", cancellationToken);

    public Task<Result<AdminJobOpportunityDto>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => TransitionStatusAsync(id, JobOpportunityStatus.Deleted, "deleted", cancellationToken);

    private async Task<Result<AdminJobOpportunityDto>> TransitionStatusAsync(Guid id, JobOpportunityStatus target, string verb, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.BAD_REQUEST, "Job id is required.");

            var entity = await _dbContext.JobOpportunities.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
            if (entity is null)
                return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.NOT_FOUND, "Job opportunity not found.");

            // Publishing a listing whose closing date has already passed
            // would put a dead job on the public site. Refuse with an
            // actionable message instead of silently succeeding.
            var now = DateTime.UtcNow;
            if (target == JobOpportunityStatus.Active && entity.ClosingDateUtc is not null && entity.ClosingDateUtc < now)
            {
                return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    "This job's closing date has passed. Clear or extend the closing date before publishing.");
            }

            var previous = entity.Status;
            if (previous == target)
                return Result<AdminJobOpportunityDto>.Success(MapToAdminDto(entity, now), $"Job is already {verb}.");

            entity.Status = target;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await LogJobAuditAsync(AuditActionType.JobOpportunityStatusChanged, entity, $"Job {verb}: {entity.Title} ({previous} → {target})");
            return Result<AdminJobOpportunityDto>.Success(MapToAdminDto(entity, now), $"Job opportunity {verb}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error transitioning job {Id} to {Status}", id, target);
            return Result<AdminJobOpportunityDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the job status.");
        }
    }

    public async Task<Result<int>> ExpireClosedJobsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var changed = await _dbContext.JobOpportunities
                .Where(j => j.Status == JobOpportunityStatus.Active && j.ClosingDateUtc != null && j.ClosingDateUtc < now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobOpportunityStatus.Expired)
                    .SetProperty(j => j.UpdatedAtUtc, now), cancellationToken);

            if (changed > 0)
                _logger.LogInformation("Expired {Count} job opportunities past their closing date.", changed);

            return Result<int>.Success(changed, changed == 0 ? "No jobs needed expiring." : $"Expired {changed} job(s).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error expiring closed jobs");
            return Result<int>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while expiring closed jobs.");
        }
    }

    // ─── Shared query helpers ─────────────────────────────────────────

    private static IQueryable<JobOpportunity> ApplyPublicFilters(IQueryable<JobOpportunity> query, JobOpportunityFilterRequestDto filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.Trim().ToLower();
            query = query.Where(j => j.Category != null && j.Category.ToLower() == category);
        }

        if (!string.IsNullOrWhiteSpace(filter.Location))
        {
            var location = filter.Location.Trim().ToLower();
            query = query.Where(j =>
                (j.Location != null && j.Location.ToLower().Contains(location))
                || (j.City != null && j.City.ToLower().Contains(location))
                || (j.Province != null && j.Province.ToLower().Contains(location)));
        }

        if (filter.WorkplaceType.HasValue)
            query = query.Where(j => j.WorkplaceType == filter.WorkplaceType.Value);

        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            var source = filter.Source.Trim().ToLower();
            query = query.Where(j => j.SourceName.ToLower() == source);
        }

        if (filter.IsFeatured.HasValue)
            query = query.Where(j => j.IsFeatured == filter.IsFeatured.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            query = query.Where(j =>
                j.Title.ToLower().Contains(s)
                || (j.CompanyName != null && j.CompanyName.ToLower().Contains(s))
                || (j.Summary != null && j.Summary.ToLower().Contains(s))
                || (j.DescriptionText != null && j.DescriptionText.ToLower().Contains(s))
                || (j.Category != null && j.Category.ToLower().Contains(s))
                || (j.Location != null && j.Location.ToLower().Contains(s)));
        }

        return query;
    }

    // Featured listings always float to the top of the default sort;
    // explicit sorts respect the caller's choice exactly.
    private static IQueryable<JobOpportunity> ApplySort(IQueryable<JobOpportunity> query, string? sort)
        => (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "closing" => query.Where(j => j.ClosingDateUtc != null).OrderBy(j => j.ClosingDateUtc),
            "title" => query.OrderBy(j => j.Title),
            "company" => query.OrderBy(j => j.CompanyName).ThenBy(j => j.Title),
            "oldest" => query.OrderBy(j => j.PostedDateUtc ?? j.ImportedAtUtc),
            _ => query.OrderByDescending(j => j.IsFeatured)
                .ThenByDescending(j => j.PostedDateUtc ?? j.ImportedAtUtc)
                .ThenByDescending(j => j.CreatedAtUtc)
        };

    private async Task<string> BuildUniqueSlugAsync(string? title, string? companyName, CancellationToken cancellationToken)
    {
        var baseSlug = JobTextUtilities.ToSlug(string.IsNullOrWhiteSpace(companyName) ? title : $"{title} {companyName}");

        // One round-trip: pull the small set of slugs that share the base
        // and resolve the suffix in memory.
        var taken = await _dbContext.JobOpportunities.AsNoTracking()
            .Where(j => j.Slug == baseSlug || j.Slug.StartsWith(baseSlug + "-"))
            .Select(j => j.Slug)
            .ToListAsync(cancellationToken);

        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        return JobTextUtilities.BuildUniqueSlug(title, companyName, candidate => set.Contains(candidate));
    }

    private async Task LogJobAuditAsync(AuditActionType action, JobOpportunity entity, string summary)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = _currentUser.UserId.HasValue ? AuditActorType.User : AuditActorType.System,
            ActionType = action,
            EntityType = AuditEntityType.JobOpportunity,
            EntityId = entity.Id,
            EntityName = entity.Title,
            Summary = summary,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    // ─── Mapping ──────────────────────────────────────────────────────

    private static JobOpportunityDto MapToPublicDto(JobOpportunity j, DateTime nowUtc, bool includeDetail, bool requiresSubscription)
    {
        var dto = new JobOpportunityDto
        {
            Id = j.Id,
            Slug = j.Slug,
            Title = j.Title,
            CompanyName = j.CompanyName,
            Location = j.Location,
            Country = j.Country,
            Province = j.Province,
            City = j.City,
            Category = j.Category,
            WorkplaceType = j.WorkplaceType,
            WorkplaceTypeLabel = j.WorkplaceType.ToString(),
            EmploymentType = j.EmploymentType,
            SalaryText = j.SalaryText,
            Summary = j.Summary,
            LogoUrl = j.LogoUrl,
            Tags = JobTextUtilities.ReadStringList(j.TagsJson),
            PostedDateUtc = j.PostedDateUtc,
            ClosingDateUtc = j.ClosingDateUtc,
            IsClosingSoon = j.ClosingDateUtc is not null && j.ClosingDateUtc >= nowUtc && j.ClosingDateUtc <= nowUtc.AddDays(ClosingSoonDays),
            IsExpired = j.ClosingDateUtc is not null && j.ClosingDateUtc < nowUtc,
            IsFeatured = j.IsFeatured,
            SourceName = j.SourceName,
            SourceUrl = j.SourceUrl,
            RequiresSubscription = requiresSubscription
        };

        if (includeDetail)
        {
            dto.DescriptionHtml = j.DescriptionHtml;
            dto.DescriptionText = j.DescriptionText;
            dto.RequirementsText = j.RequirementsText;
            dto.ApplicationInstructions = j.ApplicationInstructions;
            dto.ApplyUrl = j.ApplyUrl;
            dto.ApplyEmail = j.ApplyEmail;
        }

        return dto;
    }

    private static AdminJobOpportunityDto MapToAdminDto(JobOpportunity j, DateTime nowUtc) => new()
    {
        Id = j.Id,
        Slug = j.Slug,
        Title = j.Title,
        CompanyName = j.CompanyName,
        Location = j.Location,
        Country = j.Country,
        Province = j.Province,
        City = j.City,
        Category = j.Category,
        WorkplaceType = j.WorkplaceType,
        WorkplaceTypeLabel = j.WorkplaceType.ToString(),
        EmploymentType = j.EmploymentType,
        SalaryText = j.SalaryText,
        Summary = j.Summary,
        DescriptionHtml = j.DescriptionHtml,
        DescriptionText = j.DescriptionText,
        RequirementsText = j.RequirementsText,
        ApplicationInstructions = j.ApplicationInstructions,
        ApplyUrl = j.ApplyUrl,
        ApplyEmail = j.ApplyEmail,
        SourceUrl = j.SourceUrl,
        SourceName = j.SourceName,
        SourceId = j.SourceId,
        ExternalId = j.ExternalId,
        Fingerprint = j.Fingerprint,
        PostedDateUtc = j.PostedDateUtc,
        ClosingDateUtc = j.ClosingDateUtc,
        ImportedAtUtc = j.ImportedAtUtc,
        LastSeenAtUtc = j.LastSeenAtUtc,
        Status = j.Status,
        StatusLabel = j.Status.ToString(),
        IsFeatured = j.IsFeatured,
        IsManuallyEdited = j.IsManuallyEdited,
        IsExpired = j.ClosingDateUtc is not null && j.ClosingDateUtc < nowUtc,
        LogoUrl = j.LogoUrl,
        Tags = JobTextUtilities.ReadStringList(j.TagsJson),
        ViewCount = j.ViewCount,
        CreatedAtUtc = j.CreatedAtUtc,
        UpdatedAtUtc = j.UpdatedAtUtc
    };
}

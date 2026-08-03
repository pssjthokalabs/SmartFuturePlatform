using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs;

// Owns the single JobModuleSettings row. Created lazily on first read so
// no migration-time seed is required and a fresh environment behaves
// identically to an upgraded one.
public class JobSettingsService : IJobSettingsService
{
    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<JobSettingsService> _logger;

    public JobSettingsService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, ILogger<JobSettingsService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<JobModuleSettings> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.JobModuleSettings
            .FirstOrDefaultAsync(s => s.Id == JobModuleSettings.SingletonId, cancellationToken);
        if (existing is not null) return existing;

        // A row created before the singleton id was introduced (or by a
        // hand-written insert) still counts — adopt it rather than
        // forking a second settings row.
        var anyRow = await _dbContext.JobModuleSettings.FirstOrDefaultAsync(cancellationToken);
        if (anyRow is not null) return anyRow;

        var created = new JobModuleSettings { Id = JobModuleSettings.SingletonId };
        _dbContext.JobModuleSettings.Add(created);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two requests raced to create the singleton. The other one
            // won; re-read and use theirs.
            _dbContext.JobModuleSettings.Entry(created).State = EntityState.Detached;
            var raced = await _dbContext.JobModuleSettings
                .FirstOrDefaultAsync(s => s.Id == JobModuleSettings.SingletonId, cancellationToken);
            if (raced is not null) return raced;
            throw;
        }

        return created;
    }

    public async Task<Result<JobSettingsDto>> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await GetOrCreateAsync(cancellationToken);
            return Result<JobSettingsDto>.Success(MapToDto(settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading job settings");
            return Result<JobSettingsDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading job settings.");
        }
    }

    public async Task<Result<PublicJobSettingsDto>> GetPublicAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await GetOrCreateAsync(cancellationToken);
            return Result<PublicJobSettingsDto>.Success(new PublicJobSettingsDto
            {
                JobsModuleEnabled = settings.JobsModuleEnabled,
                JobDetailsSubscribersOnly = settings.JobDetailsSubscribersOnly,
                JobAlertsEnabled = settings.JobAlertsEnabled,
                PublicDisclaimer = settings.PublicDisclaimer
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading public job settings");
            // Public surface must never 500 over a settings read — fall
            // back to the safe defaults (module on, details public).
            return Result<PublicJobSettingsDto>.Success(new PublicJobSettingsDto { JobsModuleEnabled = true });
        }
    }

    public async Task<Result<JobSettingsDto>> UpdateAsync(UpdateJobSettingsRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<JobSettingsDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.ImportIntervalMinutes is < 15 or > 10080)
                return Result<JobSettingsDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Import interval must be between 15 minutes and 7 days.");

            if (request.ExpiredJobRetentionDays is < 0 or > 365)
                return Result<JobSettingsDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Expired job retention must be between 0 and 365 days.");

            var settings = await GetOrCreateAsync(cancellationToken);

            if (request.JobDetailsSubscribersOnly.HasValue) settings.JobDetailsSubscribersOnly = request.JobDetailsSubscribersOnly.Value;
            if (request.JobAlertsEnabled.HasValue) settings.JobAlertsEnabled = request.JobAlertsEnabled.Value;
            if (request.JobsModuleEnabled.HasValue) settings.JobsModuleEnabled = request.JobsModuleEnabled.Value;
            if (request.AutoImportEnabled.HasValue) settings.AutoImportEnabled = request.AutoImportEnabled.Value;
            if (request.ImportIntervalMinutes.HasValue) settings.ImportIntervalMinutes = request.ImportIntervalMinutes.Value;
            if (request.ExpiredJobRetentionDays.HasValue) settings.ExpiredJobRetentionDays = request.ExpiredJobRetentionDays.Value;
            if (request.PublicDisclaimer is not null) settings.PublicDisclaimer = JobTextUtilities.NullIfBlank(request.PublicDisclaimer);

            settings.UpdatedByUserId = _currentUser.UserId;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.JobSettingsUpdated,
                EntityType = AuditEntityType.JobSettings,
                EntityId = settings.Id,
                EntityName = "Job module settings",
                Summary = $"Job settings updated (subscribersOnly={settings.JobDetailsSubscribersOnly}, alerts={settings.JobAlertsEnabled}, module={settings.JobsModuleEnabled}, autoImport={settings.AutoImportEnabled})",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result<JobSettingsDto>.Success(MapToDto(settings), "Job settings updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating job settings");
            return Result<JobSettingsDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while saving job settings.");
        }
    }

    private static JobSettingsDto MapToDto(JobModuleSettings s) => new()
    {
        JobDetailsSubscribersOnly = s.JobDetailsSubscribersOnly,
        JobAlertsEnabled = s.JobAlertsEnabled,
        JobsModuleEnabled = s.JobsModuleEnabled,
        AutoImportEnabled = s.AutoImportEnabled,
        ImportIntervalMinutes = s.ImportIntervalMinutes,
        ExpiredJobRetentionDays = s.ExpiredJobRetentionDays,
        PublicDisclaimer = s.PublicDisclaimer,
        UpdatedAtUtc = s.UpdatedAtUtc,
        UpdatedByUserId = s.UpdatedByUserId
    };
}

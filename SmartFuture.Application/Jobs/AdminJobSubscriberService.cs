using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Common.Storage;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs;

public class AdminJobSubscriberService : IAdminJobSubscriberService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 200;
    private static readonly TimeSpan PreviewUrlLifetime = TimeSpan.FromMinutes(10);

    private readonly UserManager<User> _userManager;
    private readonly IAppDbContext _dbContext;
    private readonly IFileStorageService _storage;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<AdminJobSubscriberService> _logger;

    public AdminJobSubscriberService(UserManager<User> userManager, IAppDbContext dbContext, IFileStorageService storage, IAuditService auditService,
        ICurrentUserService currentUser, ILogger<AdminJobSubscriberService> logger)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _storage = storage;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<PagedResult<AdminJobSubscriberListItemDto>>> SearchAsync(AdminJobSubscriberFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminJobSubscriberFilterRequestDto();
            var page = Math.Max(1, filter.Page ?? 1);
            var pageSize = Math.Clamp(filter.PageSize ?? DefaultPageSize, 1, MaxPageSize);

            var subscriberIds = await GetUserIdsInRoleAsync(SystemRoles.JobSubscriber, cancellationToken);
            if (subscriberIds.Count == 0)
            {
                return Result<PagedResult<AdminJobSubscriberListItemDto>>.Success(
                    new PagedResult<AdminJobSubscriberListItemDto>(Array.Empty<AdminJobSubscriberListItemDto>(), page, pageSize, 0));
            }

            var customerIds = await GetUserIdsInRoleAsync(SystemRoles.Customer, cancellationToken);

            var query = _dbContext.Users.AsNoTracking().Where(u => subscriberIds.Contains(u.Id));

            // Same isolation rule the Users page uses: controlled QA test
            // accounts never appear in real operational lists.
            query = query.Where(u => !u.IsTestAccount);

            if (filter.IsAlsoCustomer == true) query = query.Where(u => customerIds.Contains(u.Id));
            if (filter.IsAlsoCustomer == false) query = query.Where(u => !customerIds.Contains(u.Id));

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var s = filter.Search.Trim().ToLower();
                query = query.Where(u =>
                    (u.FirstName + " " + u.LastName).ToLower().Contains(s)
                    || (u.Email != null && u.Email.ToLower().Contains(s))
                    || (u.PhoneNumber != null && u.PhoneNumber.Contains(filter.Search.Trim())));
            }

            // Profile-derived filters. Left-join style projection so a
            // subscriber with no profile row still lists (they enrolled
            // but never completed the wizard) unless a profile filter is
            // explicitly applied.
            var joined = query.Select(u => new
            {
                User = u,
                Profile = _dbContext.JobSubscriberProfiles.FirstOrDefault(p => p.UserId == u.Id),
                Alert = _dbContext.JobAlertPreferences.FirstOrDefault(a => a.UserId == u.Id)
            });

            if (!string.IsNullOrWhiteSpace(filter.Province))
            {
                var province = filter.Province.Trim().ToLower();
                joined = joined.Where(x => x.Profile != null && x.Profile.CurrentProvince != null && x.Profile.CurrentProvince.ToLower() == province);
            }

            if (!string.IsNullOrWhiteSpace(filter.City))
            {
                var city = filter.City.Trim().ToLower();
                joined = joined.Where(x => x.Profile != null && x.Profile.CurrentCity != null && x.Profile.CurrentCity.ToLower() == city);
            }

            if (filter.HasCv.HasValue)
            {
                joined = filter.HasCv.Value
                    ? joined.Where(x => x.Profile != null && x.Profile.CvObjectKey != null)
                    : joined.Where(x => x.Profile == null || x.Profile.CvObjectKey == null);
            }

            if (filter.IsProfileComplete.HasValue)
            {
                joined = filter.IsProfileComplete.Value
                    ? joined.Where(x => x.Profile != null && x.Profile.CompletedAtUtc != null)
                    : joined.Where(x => x.Profile == null || x.Profile.CompletedAtUtc == null);
            }

            if (filter.AlertsSubscribed.HasValue)
            {
                joined = filter.AlertsSubscribed.Value
                    ? joined.Where(x => x.Alert != null && x.Alert.IsSubscribed)
                    : joined.Where(x => x.Alert == null || !x.Alert.IsSubscribed);
            }

            var totalCount = await joined.CountAsync(cancellationToken);

            var rows = await joined
                .OrderByDescending(x => x.User.CreatedAtUtc)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .ToListAsync(cancellationToken);

            var items = rows.Select(r => MapListItem(r.User, r.Profile, r.Alert, customerIds.Contains(r.User.Id))).ToList();
            return Result<PagedResult<AdminJobSubscriberListItemDto>>.Success(new PagedResult<AdminJobSubscriberListItemDto>(items, page, pageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching job subscribers");
            return Result<PagedResult<AdminJobSubscriberListItemDto>>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading job subscribers.");
        }
    }

    public async Task<Result<AdminJobSubscriberDetailDto>> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<AdminJobSubscriberDetailDto>.Failure(ErrorCodes.BAD_REQUEST, "User id is required.");

            var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
                return Result<AdminJobSubscriberDetailDto>.Failure(ErrorCodes.NOT_FOUND, "User not found.");

            var roles = await _userManager.GetRolesAsync(user);
            if (!roles.Contains(SystemRoles.JobSubscriber, StringComparer.OrdinalIgnoreCase))
                return Result<AdminJobSubscriberDetailDto>.Failure(ErrorCodes.NOT_FOUND, "This user is not a job subscriber.");

            var profile = await _dbContext.JobSubscriberProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            var alert = await _dbContext.JobAlertPreferences.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
            var documents = await _dbContext.JobSubscriberDocuments.AsNoTracking()
                .Where(d => d.UserId == userId)
                .OrderByDescending(d => d.UploadedAtUtc)
                .ToListAsync(cancellationToken);

            var summary = MapListItem(user, profile, alert, roles.Contains(SystemRoles.Customer, StringComparer.OrdinalIgnoreCase));
            summary.Roles = roles.ToList();

            return Result<AdminJobSubscriberDetailDto>.Success(new AdminJobSubscriberDetailDto
            {
                Summary = summary,
                Profile = profile is null ? null : JobSubscriberService.MapProfileToDto(profile),
                AlertPreference = alert is null ? null : JobSubscriberService.MapAlertPreferenceToDto(alert),
                Documents = documents.Select(d => new JobSubscriberDocumentDto
                {
                    DocumentType = d.DocumentType,
                    DocumentTypeLabel = d.DocumentType.ToString(),
                    FileName = d.FileName,
                    ContentType = d.ContentType,
                    SizeBytes = d.SizeBytes,
                    UploadedAtUtc = d.UploadedAtUtc
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading job subscriber {UserId}", userId);
            return Result<AdminJobSubscriberDetailDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading the job subscriber.");
        }
    }

    public async Task<Result<FileDownloadResultDto>> DownloadDocumentAsync(Guid userId, JobSubscriberDocumentType documentType, CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = await ResolveDocumentAsync(userId, documentType, cancellationToken);
            if (!resolved.IsSuccess) return Result<FileDownloadResultDto>.Failure(resolved.Code ?? ErrorCodes.NOT_FOUND, resolved.Message);

            var (key, fileName) = resolved.Data;
            var download = await _storage.DownloadAsync(key, cancellationToken);
            if (!download.IsSuccess) return download;

            if (!string.IsNullOrWhiteSpace(fileName)) download.Data!.FileName = fileName!;

            await LogDocumentAccessAsync(userId, documentType, "downloaded");
            return download;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading {DocumentType} for subscriber {UserId}", documentType, userId);
            return Result<FileDownloadResultDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the document.");
        }
    }

    public async Task<Result<string>> GetDocumentPreviewUrlAsync(Guid userId, JobSubscriberDocumentType documentType, CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = await ResolveDocumentAsync(userId, documentType, cancellationToken);
            if (!resolved.IsSuccess) return Result<string>.Failure(resolved.Code ?? ErrorCodes.NOT_FOUND, resolved.Message);

            var url = await _storage.GetPresignedDownloadUrlAsync(resolved.Data.Key, PreviewUrlLifetime, cancellationToken);
            if (!url.IsSuccess) return url;

            await LogDocumentAccessAsync(userId, documentType, "previewed");
            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating preview URL for {DocumentType} of subscriber {UserId}", documentType, userId);
            return Result<string>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while preparing the document preview.");
        }
    }

    private async Task<Result<(string Key, string? FileName)>> ResolveDocumentAsync(Guid userId, JobSubscriberDocumentType documentType, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
            return Result<(string, string?)>.Failure(ErrorCodes.BAD_REQUEST, "User id is required.");

        var profile = await _dbContext.JobSubscriberProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var key = documentType == JobSubscriberDocumentType.Cv ? profile?.CvObjectKey : profile?.CoverLetterObjectKey;
        var name = documentType == JobSubscriberDocumentType.Cv ? profile?.CvFileName : profile?.CoverLetterFileName;

        if (string.IsNullOrWhiteSpace(key))
        {
            var label = documentType == JobSubscriberDocumentType.Cv ? "CV" : "cover letter";
            return Result<(string, string?)>.Failure(ErrorCodes.NOT_FOUND, $"This subscriber has not uploaded a {label}.");
        }

        return Result<(string, string?)>.Success((key!, name));
    }

    // CVs are personal data — every admin read leaves a trail naming the
    // admin who did it.
    private async Task LogDocumentAccessAsync(Guid subscriberUserId, JobSubscriberDocumentType documentType, string verb)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.User,
            ActionType = AuditActionType.JobSubscriberDocumentAccessed,
            EntityType = AuditEntityType.JobSubscriberProfile,
            EntityId = subscriberUserId,
            Summary = $"Admin {verb} the {documentType} of job subscriber {subscriberUserId}.",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private async Task<HashSet<Guid>> GetUserIdsInRoleAsync(string role, CancellationToken cancellationToken)
    {
        var users = await _userManager.GetUsersInRoleAsync(role);
        return users.Select(u => u.Id).ToHashSet();
    }

    private static AdminJobSubscriberListItemDto MapListItem(User user, JobSubscriberProfile? profile, JobAlertPreference? alert, bool isCustomer)
    {
        var roles = new List<string> { SystemRoles.JobSubscriber };
        if (isCustomer) roles.Insert(0, SystemRoles.Customer);

        return new AdminJobSubscriberListItemDto
        {
            UserId = user.Id,
            UserNumber = user.UserNumber,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Email = user.Email ?? string.Empty,
            PhoneNumber = user.PhoneNumber,
            AccountStatus = user.AccountStatus.ToString(),
            IsActive = user.IsActive,
            Roles = roles,
            IsCustomer = isCustomer,
            IsJobSubscriber = true,
            CurrentCity = profile?.CurrentCity,
            CurrentProvince = profile?.CurrentProvince,
            HasCv = !string.IsNullOrWhiteSpace(profile?.CvObjectKey),
            HasCoverLetter = !string.IsNullOrWhiteSpace(profile?.CoverLetterObjectKey),
            IsProfileComplete = profile?.CompletedAtUtc is not null,
            AlertsSubscribed = alert?.IsSubscribed ?? false,
            ProfileCompletedAtUtc = profile?.CompletedAtUtc,
            CreatedAtUtc = user.CreatedAtUtc
        };
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.Privacy.Dtos;
using SmartFuture.Domain.Privacy;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.CoverageRequests;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.Privacy;
using SmartFuture.Shared.Enums.SupportTickets;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Privacy;

public class PrivacyRequestService : IPrivacyRequestService
{
    private const string ErasedMarker = "[erased]";
    private const string ErasedByPrivacyRequestMarker = "[erased by privacy request]";
    private const string ErasedDomain = "@erased.local";
    private const int MaxRequestReasonLength = 2000;

    private static readonly PrivacyRequestStatus[] ActiveErasureStatuses =
    {
        PrivacyRequestStatus.Submitted,
        PrivacyRequestStatus.InReview,
        PrivacyRequestStatus.Approved
    };

    private static readonly CoverageRequestStatus[] CancellableCoverageStatuses =
    {
        CoverageRequestStatus.Submitted,
        CoverageRequestStatus.InReview,
        CoverageRequestStatus.MoreInfoRequired
    };

    private static readonly OrderStatus[] CancellableOrderStatuses =
    {
        OrderStatus.Draft,
        OrderStatus.Submitted
    };

    private static readonly SupportTicketStatus[] NonTerminalSupportTicketStatuses =
    {
        SupportTicketStatus.Open,
        SupportTicketStatus.AwaitingCustomer,
        SupportTicketStatus.AwaitingAgent,
        SupportTicketStatus.InProgress,
        SupportTicketStatus.Reopened
    };

    private static readonly NetworkAccountStatus[] NonTerminatedNetworkAccountStatuses =
    {
        NetworkAccountStatus.Pending,
        NetworkAccountStatus.Active,
        NetworkAccountStatus.Suspended,
        NetworkAccountStatus.Failed
    };

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<PrivacyRequestService> _logger;

    public PrivacyRequestService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, ILogger<PrivacyRequestService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<PagedResult<PrivacyRequestDto>>> SearchAdminAsync(PrivacyRequestFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new PrivacyRequestFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching privacy requests (admin)");
            return Result<PagedResult<PrivacyRequestDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching privacy requests.");
        }
    }

    public async Task<Result<PagedResult<PrivacyRequestDto>>> GetMineAsync(PrivacyRequestFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<PrivacyRequestDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new PrivacyRequestFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching privacy requests (customer)");
            return Result<PagedResult<PrivacyRequestDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your privacy requests.");
        }
    }

    public Task<Result<PrivacyRequestDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<PrivacyRequestDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<PrivacyRequestDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));
        return GetByIdInternalAsync(id, currentUserId, cancellationToken);
    }

    private async Task<Result<PrivacyRequestDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<PrivacyRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Privacy request id is required.");

            var query = _dbContext.PrivacyRequests
                .AsNoTracking()
                .Include(p => p.User)
                .Include(p => p.ReviewedByUser)
                .Include(p => p.CompletedByUser)
                .Where(p => p.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(p => p.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            return entity is null
                ? Result<PrivacyRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Privacy request not found.")
                : Result<PrivacyRequestDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching privacy request {Id}", id);
            return Result<PrivacyRequestDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the privacy request.");
        }
    }

    public async Task<Result<PrivacyRequestDto>> CreateMineAsync(CreatePrivacyRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PrivacyRequestDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null)
                return Result<PrivacyRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (!string.IsNullOrWhiteSpace(request.RequestReason)
                && request.RequestReason.Length > MaxRequestReasonLength)
            {
                return Result<PrivacyRequestDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"RequestReason cannot exceed {MaxRequestReasonLength} characters.");
            }

            if (request.Type == PrivacyRequestType.DataErasure)
            {
                var hasActive = await _dbContext.PrivacyRequests
                    .AsNoTracking()
                    .AnyAsync(p => p.UserId == currentUserId.Value
                                && p.Type == PrivacyRequestType.DataErasure
                                && ActiveErasureStatuses.Contains(p.Status), cancellationToken);

                if (hasActive)
                {
                    return Result<PrivacyRequestDto>.Failure(
                        ErrorCodes.CONFLICT,
                        "An active data-erasure request already exists for this user.");
                }
            }

            var entity = new PrivacyRequest
            {
                UserId = currentUserId.Value,
                Type = request.Type,
                Status = PrivacyRequestStatus.Submitted,
                RequestReason = Trim(request.RequestReason)
            };

            _dbContext.PrivacyRequests.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = currentUserId,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.AdminAction,
                EntityType = AuditEntityType.User,
                EntityId = entity.Id,
                EntityName = $"PrivacyRequest:{entity.Type}",
                Summary = $"Privacy request submitted ({entity.Type})",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result<PrivacyRequestDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Privacy request submitted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating privacy request");
            return Result<PrivacyRequestDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the privacy request.");
        }
    }

    public async Task<Result<PrivacyRequestDto>> AdminUpdateStatusAsync(Guid id, AdminUpdatePrivacyRequestStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<PrivacyRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Privacy request id is required.");

            if (request is null)
                return Result<PrivacyRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.Status == PrivacyRequestStatus.Rejected
                && string.IsNullOrWhiteSpace(request.RejectionReason))
            {
                return Result<PrivacyRequestDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "RejectionReason is required when rejecting a privacy request.");
            }

            var entity = await _dbContext.PrivacyRequests
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            if (entity is null)
                return Result<PrivacyRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Privacy request not found.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = request.Status;

            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            if (!string.IsNullOrWhiteSpace(request.RejectionReason))
                entity.RejectionReason = request.RejectionReason.Trim();

            switch (request.Status)
            {
                case PrivacyRequestStatus.InReview:
                case PrivacyRequestStatus.Approved:
                case PrivacyRequestStatus.Rejected:
                    if (entity.ReviewedAtUtc is null) entity.ReviewedAtUtc = now;
                    if (entity.ReviewedByUserId is null) entity.ReviewedByUserId = _currentUser.UserId;
                    break;

                case PrivacyRequestStatus.Completed:
                    if (entity.CompletedAtUtc is null) entity.CompletedAtUtc = now;
                    if (entity.CompletedByUserId is null) entity.CompletedByUserId = _currentUser.UserId;
                    break;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (previous != entity.Status)
            {
                await _auditService.LogAsync(new CreateAuditLogRequestDto
                {
                    ActorUserId = _currentUser.UserId,
                    ActorType = AuditActorType.Admin,
                    ActionType = AuditActionType.AdminAction,
                    EntityType = AuditEntityType.User,
                    EntityId = entity.Id,
                    EntityName = $"PrivacyRequest:{entity.Type}",
                    Summary = $"Privacy request status changed: {previous} -> {entity.Status}",
                    IpAddress = _currentUser.IpAddress,
                    UserAgent = _currentUser.UserAgent,
                    IsSuccess = true
                });
            }

            return Result<PrivacyRequestDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Privacy request updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating privacy request status {Id}", id);
            return Result<PrivacyRequestDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the privacy request status.");
        }
    }

    public async Task<Result<UserErasureResultDto>> AdminEraseUserAsync(AdminEraseUserRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return Result<UserErasureResultDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

        if (request.UserId == Guid.Empty)
            return Result<UserErasureResultDto>.Failure(ErrorCodes.VALIDATION_ERROR, "UserId is required.");

        if (!request.ConfirmErasePersonalData)
            return Result<UserErasureResultDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "ConfirmErasePersonalData must be true to perform an erasure.");

        var userId = request.UserId;
        var now = DateTime.UtcNow;
        var result = new UserErasureResultDto { UserId = userId, ErasedAtUtc = now };

        await using var transaction = await _dbContext.BeginTransactionAsync(cancellationToken);

        try
        {
            var user = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

            if (user is null)
                return Result<UserErasureResultDto>.Failure(ErrorCodes.NOT_FOUND, "User not found.");

            var shortId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var erasedEmail = $"erased-{shortId}{ErasedDomain}";

            user.Email = erasedEmail;
            user.NormalizedEmail = erasedEmail.ToUpperInvariant();
            user.UserName = erasedEmail;
            user.NormalizedUserName = erasedEmail.ToUpperInvariant();
            user.PhoneNumber = null;
            user.PhoneNumberConfirmed = false;
            user.EmailConfirmed = false;
            user.LockoutEnabled = true;
            user.LockoutEnd = DateTimeOffset.MaxValue;
            user.FirstName = ErasedMarker;
            user.LastName = ErasedMarker;
            user.IsActive = false;
            user.AccountStatus = UserAccountStatus.Inactive;

            // Customer profile
            var profile = await _dbContext.CustomerProfiles
                .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

            if (profile is not null)
            {
                profile.IdNumber = null;
                profile.AddressLine1 = null;
                profile.AddressLine2 = null;
                profile.Suburb = null;
                profile.City = null;
                profile.Province = null;
                profile.PostalCode = null;
                profile.Country = null;
                profile.Latitude = null;
                profile.Longitude = null;
                profile.Notes = null;
                profile.PreferredContactMethod = null;
                profile.AcceptsMarketing = false;
                profile.AcceptsPaymentReminders = false;
                profile.AcceptsInstallationUpdates = false;
                profile.AcceptsNetworkAlerts = false;
                result.CustomerProfilesUpdated = 1;
            }

            // Coverage requests
            var coverageRequests = await _dbContext.CoverageRequests
                .Where(c => c.UserId == userId)
                .ToListAsync(cancellationToken);

            foreach (var cr in coverageRequests)
            {
                cr.FullName = null;
                cr.Email = null;
                cr.PhoneNumber = null;
                cr.CustomerNotes = null;
                cr.AddressLine1 = ErasedMarker;
                cr.AddressLine2 = null;
                cr.PostalCode = null;
                cr.GooglePlaceId = null;
                cr.MapProviderReference = null;

                if (request.AlsoCancelPendingCoverageRequests
                    && CancellableCoverageStatuses.Contains(cr.Status))
                {
                    cr.Status = CoverageRequestStatus.Cancelled;
                    cr.ReviewedAtUtc ??= now;
                    cr.ReviewedByUserId = _currentUser.UserId;
                }
            }
            result.CoverageRequestsAnonymised = coverageRequests.Count;

            // Orders
            var orders = await _dbContext.Orders
                .Where(o => o.UserId == userId)
                .ToListAsync(cancellationToken);

            foreach (var o in orders)
            {
                o.FullName = null;
                o.Email = null;
                o.PhoneNumber = null;
                o.CustomerNotes = null;
                o.GooglePlaceId = null;
                o.MapProviderReference = null;

                if (request.AlsoCancelDraftOrSubmittedOrders
                    && CancellableOrderStatuses.Contains(o.Status))
                {
                    o.Status = OrderStatus.Cancelled;
                    o.CancelledAtUtc ??= now;
                    o.CancellationReason = "Cancelled due to privacy/data-erasure request.";
                    o.LastStatusChangedByUserId = _currentUser.UserId;
                }
            }
            result.OrdersAnonymised = orders.Count;

            // Installations (customer-side notes only; technician/admin notes preserved)
            var orderIds = orders.Select(o => o.Id).ToList();
            var installations = orderIds.Count == 0
                ? new List<Domain.Installations.Installation>()
                : await _dbContext.Installations
                    .Where(i => orderIds.Contains(i.OrderId))
                    .ToListAsync(cancellationToken);

            foreach (var ins in installations)
            {
                ins.CustomerNotes = null;
            }
            result.InstallationsAnonymised = installations.Count;

            // Support tickets owned by this user
            var tickets = await _dbContext.SupportTickets
                .Where(t => t.UserId == userId)
                .ToListAsync(cancellationToken);

            foreach (var t in tickets)
            {
                t.Subject = ErasedMarker;
                t.Description = ErasedByPrivacyRequestMarker;
                t.InternalSummary = string.IsNullOrWhiteSpace(t.InternalSummary) ? null : ErasedMarker;
                t.ResolutionSummary = string.IsNullOrWhiteSpace(t.ResolutionSummary) ? null : ErasedMarker;

                if (request.AlsoCloseOpenSupportTickets
                    && NonTerminalSupportTicketStatuses.Contains(t.Status))
                {
                    t.Status = SupportTicketStatus.Closed;
                    t.ClosedAtUtc ??= now;
                    t.LastStatusChangedByUserId = _currentUser.UserId;
                }
            }
            result.SupportTicketsAnonymised = tickets.Count;

            // Comments authored by this user — replace body only
            var ticketIds = tickets.Select(t => t.Id).ToList();
            var commentsAuthoredByUser = await _dbContext.SupportTicketComments
                .Where(c => c.AuthorUserId == userId)
                .ToListAsync(cancellationToken);

            foreach (var c in commentsAuthoredByUser)
                c.Body = ErasedByPrivacyRequestMarker;

            // Customer-visible (non-internal) comments on the user's tickets,
            // even if authored by an admin, may quote the customer's words.
            // Replace bodies of non-internal comments on user's tickets too.
            var ticketComments = ticketIds.Count == 0
                ? new List<Domain.SupportTickets.SupportTicketComment>()
                : await _dbContext.SupportTicketComments
                    .Where(c => ticketIds.Contains(c.SupportTicketId)
                             && !c.IsInternal
                             && c.AuthorUserId != userId)
                    .ToListAsync(cancellationToken);

            foreach (var c in ticketComments)
                c.Body = ErasedByPrivacyRequestMarker;

            result.SupportTicketCommentsAnonymised = commentsAuthoredByUser.Count + ticketComments.Count;

            // Network accounts attached to this user's orders
            var networkAccounts = orderIds.Count == 0
                ? new List<Domain.NetworkAccounts.NetworkAccount>()
                : await _dbContext.NetworkAccounts
                    .Where(n => orderIds.Contains(n.OrderId))
                    .ToListAsync(cancellationToken);

            var networkAnonShortId = Guid.NewGuid().ToString("N").Substring(0, 12);
            for (var i = 0; i < networkAccounts.Count; i++)
            {
                var na = networkAccounts[i];

                // Username is derived from email -> rewrite to break PII linkage.
                // We append the index so the unique index doesn't collide if multiple
                // accounts belonged to the same user.
                na.Username = $"erased-{networkAnonShortId}-{i + 1}";
                na.AdminNotes = string.IsNullOrWhiteSpace(na.AdminNotes) ? null : ErasedMarker;
                na.LastFailureReason = string.IsNullOrWhiteSpace(na.LastFailureReason) ? null : ErasedMarker;
                na.SuspensionReason = string.IsNullOrWhiteSpace(na.SuspensionReason) ? null : ErasedMarker;

                if (NonTerminatedNetworkAccountStatuses.Contains(na.Status))
                {
                    na.Status = NetworkAccountStatus.Terminated;
                    na.TerminatedAtUtc ??= now;
                    na.TerminationReason = "Terminated due to privacy/data-erasure request.";
                    na.LastStatusChangedByUserId = _currentUser.UserId;
                }
            }
            result.NetworkAccountsTerminated = networkAccounts.Count;

            // Outbound notifications targeted at the user
            var notifications = await _dbContext.OutboundNotifications
                .Where(n => n.UserId == userId)
                .ToListAsync(cancellationToken);

            foreach (var n in notifications)
            {
                n.RecipientEmail = null;
                n.RecipientPhone = null;
                n.Subject = ErasedMarker;
                n.Body = ErasedMarker;
                n.MetadataJson = null;
            }
            result.OutboundNotificationsAnonymised = notifications.Count;

            // Audit logs: never delete. Clear EntityName for rows that reference this user.
            var auditLogs = await _dbContext.AuditLogs
                .Where(a => a.ActorUserId == userId
                         || (a.EntityType == AuditEntityType.User && a.EntityId == userId))
                .ToListAsync(cancellationToken);

            foreach (var a in auditLogs)
            {
                a.EntityName = null;
                a.IpAddress = null;
                a.UserAgent = null;
            }
            result.AuditLogsTouched = auditLogs.Count;

            // Revoke all active refresh tokens
            var refreshTokens = await _dbContext.RefreshTokens
                .Where(rt => rt.UserId == userId && rt.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var rt in refreshTokens)
                rt.RevokedAtUtc = now;

            // If there's an active privacy request for this user, mark it Completed.
            var activeErasure = await _dbContext.PrivacyRequests
                .Where(p => p.UserId == userId
                         && p.Type == PrivacyRequestType.DataErasure
                         && p.Status != PrivacyRequestStatus.Completed
                         && p.Status != PrivacyRequestStatus.Rejected
                         && p.Status != PrivacyRequestStatus.Cancelled)
                .OrderByDescending(p => p.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (activeErasure is not null)
            {
                activeErasure.Status = PrivacyRequestStatus.Completed;
                activeErasure.CompletedAtUtc ??= now;
                activeErasure.CompletedByUserId ??= _currentUser.UserId;
                if (!string.IsNullOrWhiteSpace(request.AdminReason))
                    activeErasure.AdminNotes = request.AdminReason.Trim();
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            // Audit the erasure itself with NO PII in metadata.
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = AuditActorType.Admin,
                ActionType = AuditActionType.AdminAction,
                EntityType = AuditEntityType.User,
                EntityId = userId,
                EntityName = "ErasedUser",
                Summary = "User personal data erased/anonymised.",
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    customerProfilesUpdated = result.CustomerProfilesUpdated,
                    coverageRequestsAnonymised = result.CoverageRequestsAnonymised,
                    ordersAnonymised = result.OrdersAnonymised,
                    installationsAnonymised = result.InstallationsAnonymised,
                    supportTicketsAnonymised = result.SupportTicketsAnonymised,
                    supportTicketCommentsAnonymised = result.SupportTicketCommentsAnonymised,
                    outboundNotificationsAnonymised = result.OutboundNotificationsAnonymised,
                    networkAccountsTerminated = result.NetworkAccountsTerminated,
                    auditLogsTouched = result.AuditLogsTouched,
                    alsoCloseOpenSupportTickets = request.AlsoCloseOpenSupportTickets,
                    alsoCancelPendingCoverageRequests = request.AlsoCancelPendingCoverageRequests,
                    alsoCancelDraftOrSubmittedOrders = request.AlsoCancelDraftOrSubmittedOrders
                }),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            await transaction.CommitAsync(cancellationToken);

            result.Success = true;
            result.Message = "User personal data erased/anonymised. Financial and audit records preserved.";
            return Result<UserErasureResultDto>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error erasing user {UserId}", request.UserId);
            try { await transaction.RollbackAsync(cancellationToken); } catch { /* swallow */ }
            return Result<UserErasureResultDto>.Failure(
                ErrorCodes.EXCEPTION,
                "An unexpected error occurred during user erasure. The transaction was rolled back.");
        }
    }

    private IQueryable<PrivacyRequest> BuildQuery(PrivacyRequestFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.PrivacyRequests
            .AsNoTracking()
            .Include(p => p.User)
            .Include(p => p.ReviewedByUser)
            .Include(p => p.CompletedByUser)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(p => p.UserId == restrictToUserId.Value);
        else if (filter.UserId.HasValue)
            query = query.Where(p => p.UserId == filter.UserId.Value);

        if (filter.Type.HasValue)
            query = query.Where(p => p.Type == filter.Type.Value);

        if (filter.StatusFilter.HasValue)
            query = query.Where(p => p.Status == filter.StatusFilter.Value);

        if (filter.CreatedFromUtc.HasValue)
            query = query.Where(p => p.CreatedAtUtc >= filter.CreatedFromUtc.Value);

        if (filter.CreatedToUtc.HasValue)
            query = query.Where(p => p.CreatedAtUtc <= filter.CreatedToUtc.Value);

        if (filter.CompletedFromUtc.HasValue)
            query = query.Where(p => p.CompletedAtUtc != null && p.CompletedAtUtc >= filter.CompletedFromUtc.Value);

        if (filter.CompletedToUtc.HasValue)
            query = query.Where(p => p.CompletedAtUtc != null && p.CompletedAtUtc <= filter.CompletedToUtc.Value);

        if (filter.FromUtc.HasValue)
            query = query.Where(p => p.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(p => p.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(p =>
                (p.RequestReason != null && EF.Functions.Like(p.RequestReason, $"%{s}%")) ||
                (p.AdminNotes != null && EF.Functions.Like(p.AdminNotes, $"%{s}%")));
        }

        return query;
    }

    private static async Task<Result<PagedResult<PrivacyRequestDto>>> ToPagedResultAsync(IQueryable<PrivacyRequest> query, PrivacyRequestFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<PrivacyRequestDto>(
            items.Select(MapToDto).ToList(),
            filter.Page,
            filter.PageSize,
            totalCount);

        return Result<PagedResult<PrivacyRequestDto>>.Success(paged);
    }

    private async Task<PrivacyRequest?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.PrivacyRequests
            .AsNoTracking()
            .Include(p => p.User)
            .Include(p => p.ReviewedByUser)
            .Include(p => p.CompletedByUser)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PrivacyRequestDto MapToDto(PrivacyRequest p) => new()
    {
        Id = p.Id,
        UserId = p.UserId,
        UserEmail = p.User?.Email,
        Type = p.Type,
        Status = p.Status,
        RequestReason = p.RequestReason,
        AdminNotes = p.AdminNotes,
        RejectionReason = p.RejectionReason,
        ReviewedAtUtc = p.ReviewedAtUtc,
        ReviewedByUserId = p.ReviewedByUserId,
        ReviewedByUserEmail = p.ReviewedByUser?.Email,
        CompletedAtUtc = p.CompletedAtUtc,
        CompletedByUserId = p.CompletedByUserId,
        CompletedByUserEmail = p.CompletedByUser?.Email,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc
    };
}

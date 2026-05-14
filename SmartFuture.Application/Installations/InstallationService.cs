using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Installations.Dtos;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Installations;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Installations;

public class InstallationService : IInstallationService
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly OrderStatus[] InstallationCreatableOrderStatuses =
    {
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment,
        OrderStatus.PaymentReceived,
        OrderStatus.Provisioning,
        OrderStatus.Active
    };

    private static readonly InstallationStatus[] TerminalInstallationStatuses =
    {
        InstallationStatus.Completed,
        InstallationStatus.Cancelled,
        InstallationStatus.Failed
    };

    private static readonly InstallationStatus[] OrderProvisioningTriggers =
    {
        InstallationStatus.Scheduled,
        InstallationStatus.TechnicianAssigned,
        InstallationStatus.EnRoute,
        InstallationStatus.OnSite,
        InstallationStatus.Rescheduled
    };

    private static readonly OrderStatus[] OrderProvisioningEligibleSources =
    {
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment,
        OrderStatus.PaymentReceived
    };

    private static readonly OrderStatus[] OrderActivationBlockers =
    {
        OrderStatus.Active,
        OrderStatus.Cancelled,
        OrderStatus.Failed,
        OrderStatus.Rejected
    };

    // Excludes 0/O/1/I/L to avoid transcription ambiguity.
    private const string InstallationNumberAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int InstallationNumberSuffixLength = 6;
    private const int InstallationNumberMaxAttempts = 5;

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly INotificationService _notificationService;
    private readonly INetworkAccountService _networkAccountService;
    private readonly ILogger<InstallationService> _logger;

    public InstallationService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, INotificationService notificationService, INetworkAccountService networkAccountService,
        ILogger<InstallationService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _notificationService = notificationService;
        _networkAccountService = networkAccountService;
        _logger = logger;
    }

    public async Task<Result<PagedResult<InstallationDto>>> SearchAdminAsync(InstallationFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new InstallationFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching installations (admin)");
            return Result<PagedResult<InstallationDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching installations.");
        }
    }

    public async Task<Result<PagedResult<InstallationDto>>> GetMineAsync(InstallationFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<InstallationDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new InstallationFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching installations (customer)");
            return Result<PagedResult<InstallationDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your installations.");
        }
    }

    public Task<Result<InstallationDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<InstallationDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<InstallationDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    private async Task<Result<InstallationDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Installation id is required.");

            var query = _dbContext.Installations
                .AsNoTracking()
                .Include(i => i.Order)
                .Include(i => i.LastStatusChangedByUser)
                .Where(i => i.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(i => i.Order!.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            return entity is null
                ? Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Installation not found.")
                : Result<InstallationDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching installation {Id}", id);
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the installation.");
        }
    }

    public async Task<Result<InstallationDto>> CreateAsync(CreateInstallationRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.OrderId == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.VALIDATION_ERROR, "OrderId is required.");

            if (request.ScheduledForUtc.HasValue
                && request.ScheduledForUtc.Value < DateTime.UtcNow.Date)
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "ScheduledForUtc cannot be in the past.");
            }

            var technicianValidation = ValidateTechnicianContact(request.TechnicianEmail, request.TechnicianPhone);
            if (technicianValidation is not null) return technicianValidation;

            var order = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);

            if (order is null)
                return Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced order was not found.");

            if (!InstallationCreatableOrderStatuses.Contains(order.Status))
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Installations cannot be created for orders in status '{order.Status}'.");
            }

            var existingActive = await _dbContext.Installations
                .AnyAsync(i => i.OrderId == order.Id
                            && !TerminalInstallationStatuses.Contains(i.Status), cancellationToken);

            if (existingActive)
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.CONFLICT,
                    "This order already has an active installation. Cancel or complete it before creating a new one.");
            }

            var now = DateTime.UtcNow;

            var entity = new Installation
            {
                OrderId = order.Id,
                Status = request.ScheduledForUtc.HasValue
                    ? InstallationStatus.Scheduled
                    : InstallationStatus.PendingScheduling,
                Source = InstallationSource.Admin,
                ScheduledForUtc = request.ScheduledForUtc,
                TechnicianName = Trim(request.TechnicianName),
                TechnicianPhone = Trim(request.TechnicianPhone),
                TechnicianEmail = Trim(request.TechnicianEmail),
                TechnicianUserId = request.TechnicianUserId,
                AddressLine1 = order.AddressLine1,
                AddressLine2 = order.AddressLine2,
                Suburb = order.Suburb,
                City = order.City,
                Province = order.Province,
                PostalCode = order.PostalCode,
                Country = order.Country,
                Latitude = order.Latitude,
                Longitude = order.Longitude,
                GooglePlaceId = order.GooglePlaceId,
                MapProviderReference = order.MapProviderReference,
                CustomerNotes = order.CustomerNotes,
                AdminNotes = Trim(request.AdminNotes),
                LastStatusChangedByUserId = _currentUser.UserId
            };

            var installationNumber = await GenerateUniqueInstallationNumberAsync(now, cancellationToken);
            if (installationNumber is null)
                return Result<InstallationDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Could not generate a unique installation number. Please retry.");

            entity.InstallationNumber = installationNumber;

            _dbContext.Installations.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitInstallationAuditAsync(
                AuditActionType.InstallationStatusChanged,
                AuditActorType.Admin,
                entity,
                summary: $"Installation created ({entity.InstallationNumber}, status={entity.Status})",
                metadata: BuildMetadata(new
                {
                    previous = (InstallationStatus?)null,
                    newStatus = entity.Status,
                    orderId = entity.OrderId,
                    orderNumber = order.OrderNumber
                }));

            if (entity.Status == InstallationStatus.Scheduled)
                await NotifyCustomerOfInstallationAsync(entity, order, NotificationType.InstallationScheduled, cancellationToken);

            return Result<InstallationDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Installation created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating installation");
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the installation.");
        }
    }

    public async Task<Result<InstallationDto>> AdminUpdateAsync(Guid id, AdminUpdateInstallationRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Installation id is required.");

            if (request is null)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (string.IsNullOrWhiteSpace(request.AddressLine1))
                return Result<InstallationDto>.Failure(ErrorCodes.VALIDATION_ERROR, "AddressLine1 is required.");

            if (request.Latitude.HasValue && (request.Latitude.Value < -90m || request.Latitude.Value > 90m))
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Latitude must be between -90 and 90.");

            if (request.Longitude.HasValue && (request.Longitude.Value < -180m || request.Longitude.Value > 180m))
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Longitude must be between -180 and 180.");

            if (request.ScheduledForUtc.HasValue
                && request.ScheduledForUtc.Value < DateTime.UtcNow.Date)
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "ScheduledForUtc cannot be in the past.");
            }

            var technicianValidation = ValidateTechnicianContact(request.TechnicianEmail, request.TechnicianPhone);
            if (technicianValidation is not null) return technicianValidation;

            var entity = await _dbContext.Installations
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

            if (entity is null)
                return Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Installation not found.");

            if (TerminalInstallationStatuses.Contains(entity.Status))
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.CONFLICT,
                    $"Installations in status '{entity.Status}' cannot be edited. " +
                    "Use the status endpoint with the same status to append notes if needed.");
            }

            entity.ScheduledForUtc = request.ScheduledForUtc;
            entity.TechnicianName = Trim(request.TechnicianName);
            entity.TechnicianPhone = Trim(request.TechnicianPhone);
            entity.TechnicianEmail = Trim(request.TechnicianEmail);
            entity.TechnicianUserId = request.TechnicianUserId;
            entity.AddressLine1 = request.AddressLine1.Trim();
            entity.AddressLine2 = Trim(request.AddressLine2);
            entity.Suburb = Trim(request.Suburb);
            entity.City = Trim(request.City);
            entity.Province = Trim(request.Province);
            entity.PostalCode = Trim(request.PostalCode);
            entity.Country = Trim(request.Country);
            entity.Latitude = request.Latitude;
            entity.Longitude = request.Longitude;
            entity.GooglePlaceId = Trim(request.GooglePlaceId);
            entity.MapProviderReference = Trim(request.MapProviderReference);
            entity.AdminNotes = Trim(request.AdminNotes);
            entity.TechnicianNotes = Trim(request.TechnicianNotes);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<InstallationDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Installation updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating installation {Id}", id);
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the installation.");
        }
    }

    public async Task<Result<InstallationDto>> AdminUpdateStatusAsync(Guid id, AdminUpdateInstallationStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Installation id is required.");

            if (request is null)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.ScheduledForUtc.HasValue
                && request.ScheduledForUtc.Value < DateTime.UtcNow.Date)
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "ScheduledForUtc cannot be in the past.");
            }

            var entity = await _dbContext.Installations
                .Include(i => i.Order)
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

            if (entity is null)
                return Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Installation not found.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = request.Status;
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            if (!string.IsNullOrWhiteSpace(request.TechnicianNotes))
                entity.TechnicianNotes = request.TechnicianNotes.Trim();

            switch (request.Status)
            {
                case InstallationStatus.Scheduled:
                    if (request.ScheduledForUtc.HasValue)
                        entity.ScheduledForUtc = request.ScheduledForUtc;
                    break;

                case InstallationStatus.Rescheduled:
                    if (request.ScheduledForUtc.HasValue)
                    {
                        if (entity.ScheduledForUtc.HasValue)
                            entity.RescheduledFromUtc = entity.ScheduledForUtc;
                        entity.ScheduledForUtc = request.ScheduledForUtc;
                    }
                    break;

                case InstallationStatus.Completed:
                    if (entity.CompletedAtUtc is null) entity.CompletedAtUtc = now;
                    entity.CompletionNotes = Trim(request.CompletionNotes) ?? entity.CompletionNotes;
                    break;

                case InstallationStatus.Cancelled:
                    if (entity.CancelledAtUtc is null) entity.CancelledAtUtc = now;
                    entity.CancellationReason = Trim(request.CancellationReason) ?? entity.CancellationReason;
                    break;

                case InstallationStatus.Failed:
                    if (entity.FailedAtUtc is null) entity.FailedAtUtc = now;
                    entity.FailureReason = Trim(request.FailureReason) ?? entity.FailureReason;
                    break;
            }

            // Order status sync — capture previous Order state for audit
            OrderStatus? orderPrevStatus = null;
            OrderStatus? orderNewStatus = null;

            if (entity.Order is not null)
            {
                var order = entity.Order;

                if (request.Status == InstallationStatus.Completed
                    && !OrderActivationBlockers.Contains(order.Status))
                {
                    orderPrevStatus = order.Status;
                    order.Status = OrderStatus.Active;
                    if (order.ActivatedAtUtc is null) order.ActivatedAtUtc = now;
                    order.LastStatusChangedByUserId = _currentUser.UserId;
                    orderNewStatus = order.Status;
                }
                else if (OrderProvisioningTriggers.Contains(request.Status)
                         && OrderProvisioningEligibleSources.Contains(order.Status))
                {
                    orderPrevStatus = order.Status;
                    order.Status = OrderStatus.Provisioning;
                    order.LastStatusChangedByUserId = _currentUser.UserId;
                    orderNewStatus = order.Status;
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (previous != entity.Status)
            {
                await EmitInstallationAuditAsync(
                    AuditActionType.InstallationStatusChanged,
                    AuditActorType.Admin,
                    entity,
                    summary: $"Installation status changed: {previous} -> {entity.Status} ({entity.InstallationNumber})",
                    metadata: BuildMetadata(new
                    {
                        previous,
                        newStatus = entity.Status,
                        orderId = entity.OrderId,
                        orderNumber = entity.Order?.OrderNumber
                    }));
            }

            if (orderPrevStatus.HasValue && orderNewStatus.HasValue
                && orderPrevStatus.Value != orderNewStatus.Value
                && entity.Order is not null)
            {
                await EmitOrderStatusChangedAuditAsync(
                    entity.Order,
                    orderPrevStatus.Value,
                    orderNewStatus.Value,
                    triggeredByInstallationNumber: entity.InstallationNumber);
            }

            if (previous != entity.Status
                && (entity.Status == InstallationStatus.Scheduled || entity.Status == InstallationStatus.Rescheduled)
                && entity.Order is not null)
            {
                var type = entity.Status == InstallationStatus.Scheduled
                    ? NotificationType.InstallationScheduled
                    : NotificationType.InstallationStatusChanged;
                await NotifyCustomerOfInstallationAsync(entity, entity.Order, type, cancellationToken);
            }

            if (previous != entity.Status
                && entity.Status == InstallationStatus.Completed
                && entity.Order is not null)
            {
                await TryProvisionNetworkAccountAsync(entity.OrderId, entity.InstallationNumber, cancellationToken);
            }

            return Result<InstallationDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Installation status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating installation status {Id}", id);
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the installation status.");
        }
    }

    private IQueryable<Installation> BuildQuery(InstallationFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.Installations
            .AsNoTracking()
            .Include(i => i.Order)
            .Include(i => i.LastStatusChangedByUser)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(i => i.Order!.UserId == restrictToUserId.Value);

        if (filter.OrderId.HasValue)
            query = query.Where(i => i.OrderId == filter.OrderId.Value);

        if (!string.IsNullOrWhiteSpace(filter.OrderNumber))
        {
            var n = filter.OrderNumber.Trim();
            query = query.Where(i => i.Order != null && EF.Functions.Like(i.Order.OrderNumber, $"%{n}%"));
        }

        if (filter.Status.HasValue)
            query = query.Where(i => i.Status == filter.Status.Value);

        if (filter.Source.HasValue)
            query = query.Where(i => i.Source == filter.Source.Value);

        if (filter.TechnicianUserId.HasValue)
            query = query.Where(i => i.TechnicianUserId == filter.TechnicianUserId.Value);

        if (!string.IsNullOrWhiteSpace(filter.TechnicianName))
        {
            var v = filter.TechnicianName.Trim();
            query = query.Where(i => i.TechnicianName != null && EF.Functions.Like(i.TechnicianName, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(i => i.City != null && EF.Functions.Like(i.City, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Suburb))
        {
            var v = filter.Suburb.Trim();
            query = query.Where(i => i.Suburb != null && EF.Functions.Like(i.Suburb, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(i => i.Province != null && EF.Functions.Like(i.Province, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.PostalCode))
        {
            var v = filter.PostalCode.Trim();
            query = query.Where(i => i.PostalCode != null && EF.Functions.Like(i.PostalCode, $"%{v}%"));
        }

        if (filter.FromUtc.HasValue)
            query = query.Where(i => i.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(i => i.CreatedAtUtc <= filter.ToUtc.Value);

        if (filter.ScheduledFromUtc.HasValue)
            query = query.Where(i => i.ScheduledForUtc != null && i.ScheduledForUtc >= filter.ScheduledFromUtc.Value);

        if (filter.ScheduledToUtc.HasValue)
            query = query.Where(i => i.ScheduledForUtc != null && i.ScheduledForUtc <= filter.ScheduledToUtc.Value);

        if (filter.CompletedFromUtc.HasValue)
            query = query.Where(i => i.CompletedAtUtc != null && i.CompletedAtUtc >= filter.CompletedFromUtc.Value);

        if (filter.CompletedToUtc.HasValue)
            query = query.Where(i => i.CompletedAtUtc != null && i.CompletedAtUtc <= filter.CompletedToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(i =>
                EF.Functions.Like(i.InstallationNumber, $"%{s}%") ||
                EF.Functions.Like(i.AddressLine1, $"%{s}%") ||
                (i.Order != null && EF.Functions.Like(i.Order.OrderNumber, $"%{s}%")) ||
                (i.TechnicianName != null && EF.Functions.Like(i.TechnicianName, $"%{s}%")) ||
                (i.City != null && EF.Functions.Like(i.City, $"%{s}%")) ||
                (i.Suburb != null && EF.Functions.Like(i.Suburb, $"%{s}%")) ||
                (i.PostalCode != null && EF.Functions.Like(i.PostalCode, $"%{s}%")));
        }

        return query;
    }

    private static async Task<Result<PagedResult<InstallationDto>>> ToPagedResultAsync(IQueryable<Installation> query, InstallationFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(i => new InstallationDto
            {
                Id = i.Id,
                InstallationNumber = i.InstallationNumber,
                OrderId = i.OrderId,
                OrderNumber = i.Order != null ? i.Order.OrderNumber : null,
                OrderStatus = i.Order != null ? i.Order.Status : (OrderStatus?)null,
                Status = i.Status,
                Source = i.Source,
                ScheduledForUtc = i.ScheduledForUtc,
                RescheduledFromUtc = i.RescheduledFromUtc,
                CompletedAtUtc = i.CompletedAtUtc,
                CancelledAtUtc = i.CancelledAtUtc,
                FailedAtUtc = i.FailedAtUtc,
                TechnicianName = i.TechnicianName,
                TechnicianPhone = i.TechnicianPhone,
                TechnicianEmail = i.TechnicianEmail,
                TechnicianUserId = i.TechnicianUserId,
                AddressLine1 = i.AddressLine1,
                AddressLine2 = i.AddressLine2,
                Suburb = i.Suburb,
                City = i.City,
                Province = i.Province,
                PostalCode = i.PostalCode,
                Country = i.Country,
                Latitude = i.Latitude,
                Longitude = i.Longitude,
                GooglePlaceId = i.GooglePlaceId,
                CustomerNotes = i.CustomerNotes,
                AdminNotes = i.AdminNotes,
                TechnicianNotes = i.TechnicianNotes,
                CompletionNotes = i.CompletionNotes,
                FailureReason = i.FailureReason,
                CancellationReason = i.CancellationReason,
                LastStatusChangedByUserId = i.LastStatusChangedByUserId,
                LastStatusChangedByUserEmail = i.LastStatusChangedByUser != null
                    ? i.LastStatusChangedByUser.Email
                    : null,
                CreatedAtUtc = i.CreatedAtUtc,
                UpdatedAtUtc = i.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<InstallationDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<InstallationDto>>.Success(paged);
    }

    private async Task<Installation?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.Installations
            .AsNoTracking()
            .Include(i => i.Order)
            .Include(i => i.LastStatusChangedByUser)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    private static Result<InstallationDto>? ValidateTechnicianContact(string? email, string? phone)
    {
        if (!string.IsNullOrWhiteSpace(email) && !EmailRegex.IsMatch(email.Trim()))
            return Result<InstallationDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Technician email is not in a valid format.");

        if (!string.IsNullOrWhiteSpace(phone))
        {
            var p = phone.Trim();
            if (p.Length < 6 || p.Length > 50)
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Technician phone must be between 6 and 50 characters.");
        }

        return null;
    }

    private async Task<string?> GenerateUniqueInstallationNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var datePart = now.ToString("yyyyMMdd");

        for (var attempt = 0; attempt < InstallationNumberMaxAttempts; attempt++)
        {
            var candidate = $"INS-{datePart}-{GenerateRandomSuffix(InstallationNumberSuffixLength)}";
            var exists = await _dbContext.Installations
                .AnyAsync(i => i.InstallationNumber == candidate, cancellationToken);

            if (!exists) return candidate;
        }

        return null;
    }

    private static string GenerateRandomSuffix(int length)
    {
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);

        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = InstallationNumberAlphabet[buffer[i] % InstallationNumberAlphabet.Length];

        return new string(chars);
    }

    private async Task EmitInstallationAuditAsync(AuditActionType actionType, AuditActorType actorType, Installation entity, string summary, string? metadata)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = actionType,
            EntityType = AuditEntityType.Installation,
            EntityId = entity.Id,
            EntityName = entity.InstallationNumber,
            Summary = summary,
            MetadataJson = metadata,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private async Task EmitOrderStatusChangedAuditAsync(Order order, OrderStatus previous, OrderStatus newStatus, string triggeredByInstallationNumber)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.System,
            ActionType = AuditActionType.OrderStatusChanged,
            EntityType = AuditEntityType.Order,
            EntityId = order.Id,
            EntityName = order.OrderNumber,
            Summary = $"Order status changed by installation: {previous} -> {newStatus} ({order.OrderNumber})",
            MetadataJson = BuildMetadata(new
            {
                previous,
                newStatus,
                triggeredBy = "Installation",
                installationNumber = triggeredByInstallationNumber
            }),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private static string? BuildMetadata(object payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload);
        }
        catch
        {
            return null;
        }
    }

    private async Task NotifyCustomerOfInstallationAsync(Installation entity, Order order, NotificationType type, CancellationToken cancellationToken)
    {
        try
        {
            var contact = await _dbContext.Orders
                .Where(o => o.Id == order.Id)
                .Select(o => new
                {
                    o.UserId,
                    Email = o.Email ?? (o.User != null ? o.User.Email : null),
                    Phone = o.PhoneNumber ?? (o.User != null ? o.User.PhoneNumber : null)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (contact is null || string.IsNullOrWhiteSpace(contact.Email))
                return;

            var subject = type == NotificationType.InstallationScheduled
                ? $"Installation scheduled: {entity.InstallationNumber}"
                : $"Installation update: {entity.InstallationNumber}";

            var bodyParts = new List<string>
            {
                $"Your Smart Future installation has an update.",
                $"Installation number: {entity.InstallationNumber}",
                $"Order: {order.OrderNumber}",
                $"Status: {entity.Status}"
            };

            if (entity.ScheduledForUtc.HasValue)
                bodyParts.Add($"Scheduled for: {entity.ScheduledForUtc.Value:yyyy-MM-dd HH:mm} UTC");

            if (!string.IsNullOrWhiteSpace(entity.TechnicianName))
                bodyParts.Add($"Technician: {entity.TechnicianName}");

            await _notificationService.SendAsync(new SendNotificationRequestDto
            {
                UserId = contact.UserId,
                Channel = NotificationChannel.Email,
                Type = type,
                RecipientEmail = contact.Email,
                RecipientPhone = contact.Phone,
                Subject = subject,
                Body = string.Join("\n", bodyParts),
                RelatedEntityType = nameof(Installation),
                RelatedEntityId = entity.Id
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Type} notification for installation {Id}", type, entity.Id);
        }
    }

    private async Task TryProvisionNetworkAccountAsync(Guid orderId, string installationNumber, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _networkAccountService.ProvisionForOrderAsync(
                orderId, NetworkAccountSource.SystemAutomated, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Network provisioning hook (Installation {InstallationNumber}) returned non-success: {Code} {Message}",
                    installationNumber, result.Code, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Network provisioning hook (Installation {InstallationNumber}) threw",
                installationNumber);
        }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static InstallationDto MapToDto(Installation i) => new()
    {
        Id = i.Id,
        InstallationNumber = i.InstallationNumber,
        OrderId = i.OrderId,
        OrderNumber = i.Order?.OrderNumber,
        OrderStatus = i.Order?.Status,
        Status = i.Status,
        Source = i.Source,
        ScheduledForUtc = i.ScheduledForUtc,
        RescheduledFromUtc = i.RescheduledFromUtc,
        CompletedAtUtc = i.CompletedAtUtc,
        CancelledAtUtc = i.CancelledAtUtc,
        FailedAtUtc = i.FailedAtUtc,
        TechnicianName = i.TechnicianName,
        TechnicianPhone = i.TechnicianPhone,
        TechnicianEmail = i.TechnicianEmail,
        TechnicianUserId = i.TechnicianUserId,
        AddressLine1 = i.AddressLine1,
        AddressLine2 = i.AddressLine2,
        Suburb = i.Suburb,
        City = i.City,
        Province = i.Province,
        PostalCode = i.PostalCode,
        Country = i.Country,
        Latitude = i.Latitude,
        Longitude = i.Longitude,
        GooglePlaceId = i.GooglePlaceId,
        CustomerNotes = i.CustomerNotes,
        AdminNotes = i.AdminNotes,
        TechnicianNotes = i.TechnicianNotes,
        CompletionNotes = i.CompletionNotes,
        FailureReason = i.FailureReason,
        CancellationReason = i.CancellationReason,
        LastStatusChangedByUserId = i.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = i.LastStatusChangedByUser?.Email,
        CreatedAtUtc = i.CreatedAtUtc,
        UpdatedAtUtc = i.UpdatedAtUtc
    };
}

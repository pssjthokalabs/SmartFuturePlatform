using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.CoverageRequests;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Orders;

public class OrderService : IOrderService
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly OrderStatus[] CustomerCancellableStatuses =
    {
        OrderStatus.Draft,
        OrderStatus.Submitted,
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment
    };

    // Excludes 0, O, 1, I, L to avoid order-number ambiguity.
    private const string OrderNumberAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int OrderNumberSuffixLength = 6;
    private const int OrderNumberMaxAttempts = 5;

    private static readonly OrderStatus[] OrderStatusesThatTerminateNetwork =
    {
        OrderStatus.Cancelled,
        OrderStatus.Failed,
        OrderStatus.Rejected
    };

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly INotificationService _notificationService;
    private readonly INetworkAccountService _networkAccountService;
    private readonly PaymentSettings _paymentSettings;
    private readonly ILogger<OrderService> _logger;

    public OrderService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, INotificationService notificationService, INetworkAccountService networkAccountService,
        IOptions<PaymentSettings> paymentSettings, ILogger<OrderService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _notificationService = notificationService;
        _networkAccountService = networkAccountService;
        _paymentSettings = paymentSettings.Value;
        _logger = logger;
    }

    public async Task<Result<PagedResult<OrderDto>>> SearchAdminAsync(OrderFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new OrderFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching orders (admin)");
            return Result<PagedResult<OrderDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching orders.");
        }
    }

    public async Task<Result<PagedResult<OrderDto>>> GetMineAsync(OrderFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<OrderDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new OrderFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching orders (customer)");
            return Result<PagedResult<OrderDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your orders.");
        }
    }

    public Task<Result<OrderDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<OrderDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<OrderDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    private async Task<Result<OrderDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");

            var query = _dbContext.Orders
                .AsNoTracking()
                .Include(o => o.LastStatusChangedByUser)
                .Where(o => o.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(o => o.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            return entity is null
                ? Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.")
                : Result<OrderDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching order {Id}", id);
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the order.");
        }
    }

    public async Task<Result<OrderDto>> CreateMineAsync(CreateOrderRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateAddressAndContact(
                request.AddressLine1, request.Latitude, request.Longitude,
                request.Email, request.PhoneNumber, expectedInstallationDateUtc: null);
            if (validation is not null) return validation;

            if (request.ServicePackageId == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "ServicePackageId is required.");

            var package = await _dbContext.ServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.ServicePackageId, cancellationToken);

            if (package is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced service package was not found.");

            if (package.Status != ServicePackageStatus.Active)
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Referenced service package is not active and cannot be ordered.");

            if (request.CoverageRequestId.HasValue)
            {
                var coverageGuard = await ValidateCoverageRequestAsync(
                    request.CoverageRequestId.Value, currentUserId.Value, package, cancellationToken);
                if (coverageGuard is not null) return coverageGuard;
            }

            var customerProfileId = await _dbContext.CustomerProfiles
                .Where(p => p.UserId == currentUserId.Value)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(cancellationToken);

            var now = DateTime.UtcNow;

            var entity = new Order
            {
                UserId = currentUserId.Value,
                CustomerProfileId = customerProfileId,
                ServicePackageId = package.Id,
                CoverageRequestId = request.CoverageRequestId,
                Status = OrderStatus.Submitted,
                Source = OrderSource.CustomerApp,
                SubmittedAtUtc = now,
                LastStatusChangedByUserId = currentUserId,

                PackageName = package.Name,
                PackageType = package.Type,
                PackageSpeedLabel = package.SpeedLabel,
                PackageDataAllowanceLabel = package.DataAllowanceLabel,
                PackageIsUncapped = package.IsUncapped,
                PackagePrice = package.Price,
                PackageBillingCycle = package.BillingCycle,
                PackageContractMonths = package.ContractMonths,
                PackageHasFreeInstallation = package.HasFreeInstallation,
                PackageInstallationFee = package.InstallationFee,
                PackageIncludesRouter = package.IncludesRouter,

                FullName = Trim(request.FullName),
                Email = Trim(request.Email),
                PhoneNumber = Trim(request.PhoneNumber),
                AddressLine1 = request.AddressLine1.Trim(),
                AddressLine2 = Trim(request.AddressLine2),
                Suburb = Trim(request.Suburb),
                City = Trim(request.City),
                Province = Trim(request.Province),
                PostalCode = Trim(request.PostalCode),
                Country = Trim(request.Country),
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                GooglePlaceId = Trim(request.GooglePlaceId),
                MapProviderReference = Trim(request.MapProviderReference),
                CustomerNotes = Trim(request.CustomerNotes)
            };

            var orderNumber = await GenerateUniqueOrderNumberAsync(now, cancellationToken);
            if (orderNumber is null)
                return Result<OrderDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Could not generate a unique order number. Please retry.");

            entity.OrderNumber = orderNumber;

            _dbContext.Orders.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.OrderCreated,
                AuditActorType.User,
                entity,
                summary: $"Order submitted: {entity.OrderNumber} ({entity.PackageName})",
                metadata: BuildMetadata(new
                {
                    servicePackageId = entity.ServicePackageId,
                    coverageRequestId = entity.CoverageRequestId,
                    packageName = entity.PackageName,
                    packagePrice = entity.PackagePrice
                }));

            // Optional mock-checkout invoice + payment persistence.
            // Gated by `PaymentSettings:MockCheckoutEnabled` (UAT only),
            // and only fires when the client sent the Ozow mock-checkout
            // hint. Service activation is unchanged: the order stays
            // Submitted and admin still owns activation.
            if (_paymentSettings.MockCheckoutEnabled
                && string.Equals(request.MockCheckoutPaymentProvider, "Ozow", StringComparison.OrdinalIgnoreCase))
            {
                await PersistMockCheckoutAsync(entity, request, now, cancellationToken);
            }

            await TryNotifyAsync(
                userId: entity.UserId,
                type: NotificationType.OrderCreated,
                email: entity.Email,
                phone: entity.PhoneNumber,
                subject: $"Order received: {entity.OrderNumber}",
                body: $"Thank you for your order with Smart Future.\n\nOrder number: {entity.OrderNumber}\nPackage: {entity.PackageName}\nMonthly price: {entity.PackagePrice:0.00}\n\nWe will be in touch with next steps shortly.",
                relatedEntityType: nameof(Order),
                relatedEntityId: entity.Id,
                cancellationToken: cancellationToken);

            return Result<OrderDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Order submitted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating order");
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the order.");
        }
    }

    public async Task<Result<OrderDto>> AdminUpdateAsync(Guid id, AdminUpdateOrderRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");

            if (request is null)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateAddressAndContact(
                request.AddressLine1, request.Latitude, request.Longitude,
                request.Email, request.PhoneNumber, request.ExpectedInstallationDateUtc);
            if (validation is not null) return validation;

            var entity = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

            if (entity is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            entity.FullName = Trim(request.FullName);
            entity.Email = Trim(request.Email);
            entity.PhoneNumber = Trim(request.PhoneNumber);
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
            entity.ExpectedInstallationDateUtc = request.ExpectedInstallationDateUtc;
            entity.AdminNotes = Trim(request.AdminNotes);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<OrderDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Order updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating order {Id}", id);
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the order.");
        }
    }

    public async Task<Result<OrderDto>> AdminUpdateStatusAsync(Guid id, AdminUpdateOrderStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");

            if (request is null)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.ExpectedInstallationDateUtc.HasValue
                && request.ExpectedInstallationDateUtc.Value < DateTime.UtcNow.Date)
            {
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "ExpectedInstallationDateUtc cannot be in the past.");
            }

            var entity = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

            if (entity is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = request.Status;
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            if (request.ExpectedInstallationDateUtc.HasValue)
                entity.ExpectedInstallationDateUtc = request.ExpectedInstallationDateUtc;

            switch (request.Status)
            {
                case OrderStatus.Confirmed:
                    if (entity.ConfirmedAtUtc is null) entity.ConfirmedAtUtc = now;
                    break;
                case OrderStatus.Active:
                    if (entity.ActivatedAtUtc is null) entity.ActivatedAtUtc = now;
                    break;
                case OrderStatus.Cancelled:
                    if (entity.CancelledAtUtc is null) entity.CancelledAtUtc = now;
                    entity.CancellationReason = Trim(request.CancellationReason) ?? entity.CancellationReason;
                    break;
                case OrderStatus.Failed:
                    entity.FailureReason = Trim(request.FailureReason) ?? entity.FailureReason;
                    break;
                case OrderStatus.Rejected:
                    entity.RejectionReason = Trim(request.RejectionReason) ?? entity.RejectionReason;
                    break;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (previous != entity.Status)
            {
                await EmitAuditAsync(
                    AuditActionType.OrderStatusChanged,
                    AuditActorType.Admin,
                    entity,
                    summary: $"Order status changed: {previous} -> {entity.Status} ({entity.OrderNumber})",
                    metadata: BuildMetadata(new { previous, newStatus = entity.Status }));
            }

            if (previous != entity.Status && OrderStatusesThatTerminateNetwork.Contains(entity.Status))
            {
                await TryTerminateNetworkForOrderAsync(
                    entity.Id, entity.OrderNumber, entity.Status, NetworkAccountSource.AdminManual, cancellationToken);
            }

            return Result<OrderDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Order status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating order status {Id}", id);
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the order status.");
        }
    }

    public async Task<Result> CancelMineAsync(Guid id, string? cancellationReason = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (id == Guid.Empty)
                return Result.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");

            var entity = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == id && o.UserId == currentUserId.Value, cancellationToken);

            if (entity is null)
                return Result.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            if (!CustomerCancellableStatuses.Contains(entity.Status))
                return Result.Failure(
                    ErrorCodes.CONFLICT,
                    $"Orders in status '{entity.Status}' cannot be cancelled by the customer.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = OrderStatus.Cancelled;
            entity.CancelledAtUtc = now;
            entity.LastStatusChangedByUserId = currentUserId;
            entity.CancellationReason = Trim(cancellationReason) ?? entity.CancellationReason;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.OrderStatusChanged,
                AuditActorType.User,
                entity,
                summary: $"Order cancelled by customer: {previous} -> {entity.Status} ({entity.OrderNumber})",
                metadata: BuildMetadata(new { previous, newStatus = entity.Status }));

            await TryTerminateNetworkForOrderAsync(
                entity.Id, entity.OrderNumber, entity.Status, NetworkAccountSource.SystemAutomated, cancellationToken);

            return Result.Success("Order cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error cancelling order {Id}", id);
            return Result.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while cancelling the order.");
        }
    }

    private IQueryable<Order> BuildQuery(OrderFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.LastStatusChangedByUser)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(o => o.UserId == restrictToUserId.Value);
        else if (filter.UserId.HasValue)
            query = query.Where(o => o.UserId == filter.UserId.Value);

        if (filter.CustomerProfileId.HasValue)
            query = query.Where(o => o.CustomerProfileId == filter.CustomerProfileId.Value);

        if (filter.ServicePackageId.HasValue)
            query = query.Where(o => o.ServicePackageId == filter.ServicePackageId.Value);

        if (filter.CoverageRequestId.HasValue)
            query = query.Where(o => o.CoverageRequestId == filter.CoverageRequestId.Value);

        if (filter.StatusFilter.HasValue)
            query = query.Where(o => o.Status == filter.StatusFilter.Value);

        if (filter.Source.HasValue)
            query = query.Where(o => o.Source == filter.Source.Value);

        if (filter.PackageType.HasValue)
            query = query.Where(o => o.PackageType == filter.PackageType.Value);

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(o => o.City != null && EF.Functions.Like(o.City, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Suburb))
        {
            var v = filter.Suburb.Trim();
            query = query.Where(o => o.Suburb != null && EF.Functions.Like(o.Suburb, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(o => o.Province != null && EF.Functions.Like(o.Province, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.PostalCode))
        {
            var v = filter.PostalCode.Trim();
            query = query.Where(o => o.PostalCode != null && EF.Functions.Like(o.PostalCode, $"%{v}%"));
        }

        if (filter.FromUtc.HasValue)
            query = query.Where(o => o.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(o => o.CreatedAtUtc <= filter.ToUtc.Value);

        if (filter.SubmittedFromUtc.HasValue)
            query = query.Where(o => o.SubmittedAtUtc != null && o.SubmittedAtUtc >= filter.SubmittedFromUtc.Value);

        if (filter.SubmittedToUtc.HasValue)
            query = query.Where(o => o.SubmittedAtUtc != null && o.SubmittedAtUtc <= filter.SubmittedToUtc.Value);

        if (filter.ExpectedInstallationFromUtc.HasValue)
            query = query.Where(o => o.ExpectedInstallationDateUtc != null
                                      && o.ExpectedInstallationDateUtc >= filter.ExpectedInstallationFromUtc.Value);

        if (filter.ExpectedInstallationToUtc.HasValue)
            query = query.Where(o => o.ExpectedInstallationDateUtc != null
                                      && o.ExpectedInstallationDateUtc <= filter.ExpectedInstallationToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(o =>
                EF.Functions.Like(o.OrderNumber, $"%{s}%") ||
                EF.Functions.Like(o.PackageName, $"%{s}%") ||
                EF.Functions.Like(o.AddressLine1, $"%{s}%") ||
                (o.FullName != null && EF.Functions.Like(o.FullName, $"%{s}%")) ||
                (o.Email != null && EF.Functions.Like(o.Email, $"%{s}%")) ||
                (o.City != null && EF.Functions.Like(o.City, $"%{s}%")) ||
                (o.Suburb != null && EF.Functions.Like(o.Suburb, $"%{s}%")) ||
                (o.PostalCode != null && EF.Functions.Like(o.PostalCode, $"%{s}%")));
        }

        return query;
    }

    private static async Task<Result<PagedResult<OrderDto>>> ToPagedResultAsync(IQueryable<Order> query, OrderFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(o => new OrderDto
            {
                Id = o.Id,
                OrderNumber = o.OrderNumber,
                UserId = o.UserId,
                CustomerProfileId = o.CustomerProfileId,
                ServicePackageId = o.ServicePackageId,
                CoverageRequestId = o.CoverageRequestId,
                Status = o.Status,
                Source = o.Source,
                PackageName = o.PackageName,
                PackageType = o.PackageType,
                PackageSpeedLabel = o.PackageSpeedLabel,
                PackageDataAllowanceLabel = o.PackageDataAllowanceLabel,
                PackageIsUncapped = o.PackageIsUncapped,
                PackagePrice = o.PackagePrice,
                PackageBillingCycle = o.PackageBillingCycle,
                PackageContractMonths = o.PackageContractMonths,
                PackageHasFreeInstallation = o.PackageHasFreeInstallation,
                PackageInstallationFee = o.PackageInstallationFee,
                PackageIncludesRouter = o.PackageIncludesRouter,
                FullName = o.FullName,
                Email = o.Email,
                PhoneNumber = o.PhoneNumber,
                AddressLine1 = o.AddressLine1,
                AddressLine2 = o.AddressLine2,
                Suburb = o.Suburb,
                City = o.City,
                Province = o.Province,
                PostalCode = o.PostalCode,
                Country = o.Country,
                Latitude = o.Latitude,
                Longitude = o.Longitude,
                GooglePlaceId = o.GooglePlaceId,
                CustomerNotes = o.CustomerNotes,
                AdminNotes = o.AdminNotes,
                SubmittedAtUtc = o.SubmittedAtUtc,
                ConfirmedAtUtc = o.ConfirmedAtUtc,
                CancelledAtUtc = o.CancelledAtUtc,
                ActivatedAtUtc = o.ActivatedAtUtc,
                ExpectedInstallationDateUtc = o.ExpectedInstallationDateUtc,
                LastStatusChangedByUserId = o.LastStatusChangedByUserId,
                LastStatusChangedByUserEmail = o.LastStatusChangedByUser != null
                    ? o.LastStatusChangedByUser.Email
                    : null,
                CancellationReason = o.CancellationReason,
                FailureReason = o.FailureReason,
                RejectionReason = o.RejectionReason,
                CreatedAtUtc = o.CreatedAtUtc,
                UpdatedAtUtc = o.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<OrderDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<OrderDto>>.Success(paged);
    }

    private async Task<Order?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.LastStatusChangedByUser)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    private async Task<Result<OrderDto>?> ValidateCoverageRequestAsync(Guid coverageRequestId, Guid currentUserId, ServicePackage package, CancellationToken cancellationToken)
    {
        var coverage = await _dbContext.CoverageRequests
            .AsNoTracking()
            .Where(c => c.Id == coverageRequestId)
            .Select(c => new
            {
                c.Id,
                c.UserId,
                c.Status,
                c.ServicePackageId,
                c.RequestedServiceType
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (coverage is null)
            return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced coverage request was not found.");

        if (coverage.UserId != currentUserId)
            return Result<OrderDto>.Failure(
                ErrorCodes.FORBIDDEN, "Coverage request does not belong to the current user.");

        if (coverage.Status != CoverageRequestStatus.Available)
            return Result<OrderDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Coverage request must be in 'Available' status to place an order against it.");

        if (coverage.ServicePackageId.HasValue && coverage.ServicePackageId.Value != package.Id)
        {
            return Result<OrderDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Coverage request is linked to a different service package.");
        }

        if (!coverage.ServicePackageId.HasValue && coverage.RequestedServiceType.HasValue
            && coverage.RequestedServiceType.Value != package.Type)
        {
            return Result<OrderDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Selected service package does not match the requested service type on the coverage request.");
        }

        return null;
    }

    private static Result<OrderDto>? ValidateAddressAndContact(string? addressLine1, decimal? latitude, decimal? longitude, string? email, string? phoneNumber,
        DateTime? expectedInstallationDateUtc)
    {
        if (string.IsNullOrWhiteSpace(addressLine1))
            return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "AddressLine1 is required.");

        if (latitude.HasValue && (latitude.Value < -90m || latitude.Value > 90m))
            return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Latitude must be between -90 and 90.");

        if (longitude.HasValue && (longitude.Value < -180m || longitude.Value > 180m))
            return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Longitude must be between -180 and 180.");

        if (!string.IsNullOrWhiteSpace(email) && !EmailRegex.IsMatch(email.Trim()))
            return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Email is not in a valid format.");

        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            var p = phoneNumber.Trim();
            if (p.Length < 6 || p.Length > 50)
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "PhoneNumber must be between 6 and 50 characters.");
        }

        if (expectedInstallationDateUtc.HasValue
            && expectedInstallationDateUtc.Value < DateTime.UtcNow.Date)
        {
            return Result<OrderDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "ExpectedInstallationDateUtc cannot be in the past.");
        }

        return null;
    }

    // ─── Mock-checkout invoice + payment persistence ───────────────────────
    //
    // **UAT-only.** Creates an Invoice + Payment for the just-submitted
    // order using a server-authoritative amount derived from the package
    // snapshot stored on the order (so the client can't influence the
    // recorded total). The order's status is NOT touched — admins still
    // handle activation. Audited as a mock-checkout entry on both the
    // invoice and the payment so this can be filtered out of real
    // revenue reports later.
    private async Task PersistMockCheckoutAsync(Order order, CreateOrderRequestDto request, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var installationFee = order.PackageHasFreeInstallation
                ? 0m
                : (order.PackageInstallationFee ?? 0m);
            var total = order.PackagePrice + installationFee;

            var invoiceNumber = await GenerateUniqueBillingNumberAsync("INV", isInvoice: true, now, cancellationToken)
                ?? $"INV-{now:yyyyMMdd}-MOCK";
            var paymentNumber = await GenerateUniqueBillingNumberAsync("PAY", isInvoice: false, now, cancellationToken)
                ?? $"PAY-{now:yyyyMMdd}-MOCK";

            var reference = Truncate(request.MockCheckoutPaymentReference, 200);

            var invoice = new Invoice
            {
                InvoiceNumber = invoiceNumber,
                OrderId = order.Id,
                Status = InvoiceStatus.Paid,
                SubtotalAmount = total,
                TaxAmount = 0m,
                TotalAmount = total,
                AmountPaid = total,
                BalanceDue = 0m,
                CurrencyCode = "ZAR",
                IssuedAtUtc = now,
                DueAtUtc = now,
                PaidAtUtc = now,
                Notes = "Mock checkout (UAT) — auto-generated by the customer order flow.",
                ExternalReference = reference,
                LastStatusChangedByUserId = order.UserId
            };
            _dbContext.Invoices.Add(invoice);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var payment = new Payment
            {
                PaymentNumber = paymentNumber,
                InvoiceId = invoice.Id,
                Status = PaymentStatus.Completed,
                Method = PaymentMethodType.Gateway,
                Amount = total,
                CurrencyCode = "ZAR",
                PaidAtUtc = now,
                GatewayName = "Ozow",
                GatewayReference = reference,
                ExternalReference = "Mock checkout (UAT)",
                Notes = "Auto-generated by Phase 28 mock-checkout path; not a real payment.",
                LastStatusChangedByUserId = order.UserId
            };
            _dbContext.Payments.Add(payment);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = order.UserId,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.InvoiceCreated,
                EntityType = AuditEntityType.Invoice,
                EntityId = invoice.Id,
                EntityName = invoice.InvoiceNumber,
                Summary = $"Mock-checkout invoice {invoice.InvoiceNumber} created (order {order.OrderNumber}, total {total:0.00}).",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true,
                MetadataJson = BuildMetadata(new
                {
                    orderId = order.Id,
                    orderNumber = order.OrderNumber,
                    mockCheckout = true,
                    provider = "Ozow",
                    reference,
                    total
                })
            });

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = order.UserId,
                ActorType = AuditActorType.User,
                ActionType = AuditActionType.PaymentStatusChanged,
                EntityType = AuditEntityType.Payment,
                EntityId = payment.Id,
                EntityName = payment.PaymentNumber,
                Summary = $"Mock-checkout payment {payment.PaymentNumber} recorded as Completed (Ozow, {total:0.00}).",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true,
                MetadataJson = BuildMetadata(new
                {
                    invoiceId = invoice.Id,
                    orderId = order.Id,
                    mockCheckout = true,
                    provider = "Ozow",
                    reference,
                    amount = total
                })
            });
        }
        catch (Exception ex)
        {
            // The order itself is already saved — don't fail the request
            // because the optional invoice/payment write didn't land.
            // Operators see this in logs; the customer's order is intact.
            _logger.LogError(ex,
                "Mock-checkout invoice/payment persistence failed for order {OrderId}. Order remains valid.",
                order.Id);
        }
    }

    private async Task<string?> GenerateUniqueBillingNumberAsync(string prefix, bool isInvoice, DateTime now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = BillingNumberGenerator.BuildCandidate(prefix, now);
            var exists = isInvoice
                ? await _dbContext.Invoices.AnyAsync(i => i.InvoiceNumber == candidate, cancellationToken)
                : await _dbContext.Payments.AnyAsync(p => p.PaymentNumber == candidate, cancellationToken);
            if (!exists) return candidate;
        }
        return null;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }

    private async Task<string?> GenerateUniqueOrderNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var datePart = now.ToString("yyyyMMdd");

        for (var attempt = 0; attempt < OrderNumberMaxAttempts; attempt++)
        {
            var candidate = $"SF-{datePart}-{GenerateRandomSuffix(OrderNumberSuffixLength)}";
            var exists = await _dbContext.Orders.AnyAsync(o => o.OrderNumber == candidate, cancellationToken);
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
            chars[i] = OrderNumberAlphabet[buffer[i] % OrderNumberAlphabet.Length];

        return new string(chars);
    }

    private async Task EmitAuditAsync(AuditActionType actionType, AuditActorType actorType, Order entity, string summary, string? metadata)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = actionType,
            EntityType = AuditEntityType.Order,
            EntityId = entity.Id,
            EntityName = entity.OrderNumber,
            Summary = summary,
            MetadataJson = metadata,
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

    private async Task TryNotifyAsync(Guid userId, NotificationType type, string? email, string? phone, string subject,
        string body, string relatedEntityType, Guid relatedEntityId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(email))
                return;

            await _notificationService.SendAsync(new SendNotificationRequestDto
            {
                UserId = userId,
                Channel = NotificationChannel.Email,
                Type = type,
                RecipientEmail = email,
                RecipientPhone = phone,
                Subject = subject,
                Body = body,
                RelatedEntityType = relatedEntityType,
                RelatedEntityId = relatedEntityId
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Type} notification for {EntityType} {EntityId}",
                type, relatedEntityType, relatedEntityId);
        }
    }

    private async Task TryTerminateNetworkForOrderAsync(Guid orderId, string orderNumber, OrderStatus newStatus, NetworkAccountSource source, CancellationToken cancellationToken)
    {
        try
        {
            var reason = $"Order {orderNumber} reached terminal status '{newStatus}'.";
            var result = await _networkAccountService.TerminateForOrderAsync(
                orderId, reason, source, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Network termination hook (Order {OrderNumber}) returned non-success: {Code} {Message}",
                    orderNumber, result.Code, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Network termination hook (Order {OrderNumber}) threw", orderNumber);
        }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OrderDto MapToDto(Order o) => new()
    {
        Id = o.Id,
        OrderNumber = o.OrderNumber,
        UserId = o.UserId,
        CustomerProfileId = o.CustomerProfileId,
        ServicePackageId = o.ServicePackageId,
        CoverageRequestId = o.CoverageRequestId,
        Status = o.Status,
        Source = o.Source,
        PackageName = o.PackageName,
        PackageType = o.PackageType,
        PackageSpeedLabel = o.PackageSpeedLabel,
        PackageDataAllowanceLabel = o.PackageDataAllowanceLabel,
        PackageIsUncapped = o.PackageIsUncapped,
        PackagePrice = o.PackagePrice,
        PackageBillingCycle = o.PackageBillingCycle,
        PackageContractMonths = o.PackageContractMonths,
        PackageHasFreeInstallation = o.PackageHasFreeInstallation,
        PackageInstallationFee = o.PackageInstallationFee,
        PackageIncludesRouter = o.PackageIncludesRouter,
        FullName = o.FullName,
        Email = o.Email,
        PhoneNumber = o.PhoneNumber,
        AddressLine1 = o.AddressLine1,
        AddressLine2 = o.AddressLine2,
        Suburb = o.Suburb,
        City = o.City,
        Province = o.Province,
        PostalCode = o.PostalCode,
        Country = o.Country,
        Latitude = o.Latitude,
        Longitude = o.Longitude,
        GooglePlaceId = o.GooglePlaceId,
        CustomerNotes = o.CustomerNotes,
        AdminNotes = o.AdminNotes,
        SubmittedAtUtc = o.SubmittedAtUtc,
        ConfirmedAtUtc = o.ConfirmedAtUtc,
        CancelledAtUtc = o.CancelledAtUtc,
        ActivatedAtUtc = o.ActivatedAtUtc,
        ExpectedInstallationDateUtc = o.ExpectedInstallationDateUtc,
        LastStatusChangedByUserId = o.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = o.LastStatusChangedByUser?.Email,
        CancellationReason = o.CancellationReason,
        FailureReason = o.FailureReason,
        RejectionReason = o.RejectionReason,
        CreatedAtUtc = o.CreatedAtUtc,
        UpdatedAtUtc = o.UpdatedAtUtc
    };
}

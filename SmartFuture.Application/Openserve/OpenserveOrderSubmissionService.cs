using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Openserve Product Order submission + retry (brief Priority 1).
/// Deliberately does NOT process callbacks/events/reconciliation
/// results — that's <see cref="IOpenserveOrderUpdatePipeline"/>
/// (Priority 3), the single shared handler every update source funnels
/// through.
/// </summary>
public class OpenserveOrderSubmissionService : IOpenserveOrderSubmissionService
{
    private readonly IAppDbContext _dbContext;
    private readonly IOpenserveApiClient _client;
    private readonly IOpenserveSubscriberReferenceGenerator _subscriberReferenceGenerator;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<OpenserveOrderSubmissionService> _logger;

    public OpenserveOrderSubmissionService(
        IAppDbContext dbContext,
        IOpenserveApiClient client,
        IOpenserveSubscriberReferenceGenerator subscriberReferenceGenerator,
        IOpenserveRuntimeConfigProvider configProvider,
        IAuditService auditService,
        ICurrentUserService currentUser,
        ILogger<OpenserveOrderSubmissionService> logger)
    {
        _dbContext = dbContext;
        _client = client;
        _subscriberReferenceGenerator = subscriberReferenceGenerator;
        _configProvider = configProvider;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result> TrySubmitForOrderAsync(Guid orderId, Guid networkAccountId, CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        if (!settings.Enabled)
        {
            _logger.LogDebug("[Openserve] Submission skipped for order {OrderId} — OpenserveFulfilment:Enabled is false.", orderId);
            return Result.Success("Openserve integration disabled — skipped.");
        }

        try
        {
            var order = await _dbContext.Orders
                .Include(o => o.ServicePackage)
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order is null) return Result.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            if (order.PackageType != Shared.Enums.ServicePackages.ServicePackageType.Fibre)
            {
                // Defensive — callers already filter to Fibre, but this
                // service must never touch Security/Voice/LTE/Wireless.
                return Result.Success("Not a Fibre order — Openserve submission does not apply.");
            }

            // Idempotency: once an OpenserveOrder row exists for this
            // Order (in ANY state), the automatic trigger is a no-op.
            // A Failed submission only advances via AdminRetrySubmissionAsync.
            var existing = await _dbContext.OpenserveOrders.FirstOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);
            if (existing is not null)
            {
                return Result.Success($"Openserve order already exists for this order (status={existing.NormalizedStatus}).");
            }

            var networkAccount = await _dbContext.NetworkAccounts.FirstOrDefaultAsync(n => n.Id == networkAccountId, cancellationToken);
            if (networkAccount is null) return Result.Failure(ErrorCodes.NOT_FOUND, "Network account not found.");

            var mapping = await _dbContext.PackageOpenserveMappings
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.ServicePackageId == order.ServicePackageId && m.IsEnabled, cancellationToken);

            var externalReferenceNumber = $"SF-{order.OrderNumber}";

            // Reserve the subscriber reference once, on the durable
            // NetworkAccount row — persists across every retry.
            if (string.IsNullOrWhiteSpace(networkAccount.OpenserveSubscriberReferenceNumber))
            {
                networkAccount.OpenserveSubscriberReferenceNumber = _subscriberReferenceGenerator.Generate(networkAccount, order);
                networkAccount.UpdatedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            var blockingReason = DetermineBlockingReason(order, mapping);

            var openserveOrder = new OpenserveOrder
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                ExternalReferenceNumber = externalReferenceNumber,
                SubscriberReferenceNumber = networkAccount.OpenserveSubscriberReferenceNumber,
                OrderType = "Sales Order",
                PackageOpenserveMappingId = mapping?.Id,
                NormalizedStatus = blockingReason is null ? OpenserveProvisioningStatus.Submitting : OpenserveProvisioningStatus.Failed,
                LastFailureMessage = blockingReason,
                RetryCount = 0,
                CreatedAtUtc = DateTime.UtcNow
            };
            _dbContext.OpenserveOrders.Add(openserveOrder);
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (blockingReason is not null)
            {
                await EmitAuditAsync(AuditActionType.OpenserveOrderSubmissionFailed, openserveOrder, order,
                    $"Openserve submission blocked for order {order.OrderNumber}: {blockingReason}");
                return Result.Success($"Openserve order record created but blocked: {blockingReason}");
            }

            await ExecuteSubmissionCallAsync(openserveOrder, order, mapping!, networkAccount, cancellationToken);
            return Result.Success("Openserve order submission attempted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error submitting order {OrderId} to Openserve.", orderId);
            return Result.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while submitting to Openserve.");
        }
    }

    public async Task<Result<OpenserveOrderDto>> AdminRetrySubmissionAsync(Guid openserveOrderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = _configProvider.Current;
            if (!settings.Enabled)
                return Result<OpenserveOrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "OpenserveFulfilment integration is disabled.");

            var openserveOrder = await _dbContext.OpenserveOrders.FirstOrDefaultAsync(o => o.Id == openserveOrderId, cancellationToken);
            if (openserveOrder is null) return Result<OpenserveOrderDto>.Failure(ErrorCodes.NOT_FOUND, "Openserve order not found.");

            if (openserveOrder.NormalizedStatus is not (OpenserveProvisioningStatus.Failed or OpenserveProvisioningStatus.NotSubmitted))
                return Result<OpenserveOrderDto>.Failure(
                    ErrorCodes.CONFLICT,
                    $"Only a Failed submission can be retried (current status: {openserveOrder.NormalizedStatus}).");

            var order = await _dbContext.Orders.Include(o => o.ServicePackage).FirstOrDefaultAsync(o => o.Id == openserveOrder.OrderId, cancellationToken);
            if (order is null) return Result<OpenserveOrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            var networkAccount = await _dbContext.NetworkAccounts.FirstOrDefaultAsync(n => n.OrderId == order.Id, cancellationToken);
            if (networkAccount is null) return Result<OpenserveOrderDto>.Failure(ErrorCodes.NOT_FOUND, "Network account not found for this order.");

            var mapping = openserveOrder.PackageOpenserveMappingId.HasValue
                ? await _dbContext.PackageOpenserveMappings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == openserveOrder.PackageOpenserveMappingId.Value, cancellationToken)
                : await _dbContext.PackageOpenserveMappings.AsNoTracking().FirstOrDefaultAsync(m => m.ServicePackageId == order.ServicePackageId && m.IsEnabled, cancellationToken);

            var blockingReason = DetermineBlockingReason(order, mapping);
            if (blockingReason is not null)
            {
                openserveOrder.LastFailureMessage = blockingReason;
                openserveOrder.RetryCount += 1;
                openserveOrder.UpdatedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
                return Result<OpenserveOrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, blockingReason);
            }

            openserveOrder.PackageOpenserveMappingId = mapping!.Id;
            await ExecuteSubmissionCallAsync(openserveOrder, order, mapping, networkAccount, cancellationToken);

            await EmitAuditAsync(
                AuditActionType.OpenserveManualRetry, openserveOrder, order,
                $"Admin retried Openserve submission for order {order.OrderNumber}.");

            var dto = await MapToDtoAsync(openserveOrder.Id, cancellationToken);
            return dto is null
                ? Result<OpenserveOrderDto>.Failure(ErrorCodes.EXCEPTION, "Retry completed but the record could not be reloaded.")
                : Result<OpenserveOrderDto>.Success(dto, "Retry attempted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrying Openserve submission {Id}.", openserveOrderId);
            return Result<OpenserveOrderDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while retrying the submission.");
        }
    }

    public async Task<Result<OpenserveOrderDto>> GetByIdAsync(Guid openserveOrderId, CancellationToken cancellationToken = default)
    {
        var dto = await MapToDtoAsync(openserveOrderId, cancellationToken);
        return dto is null
            ? Result<OpenserveOrderDto>.Failure(ErrorCodes.NOT_FOUND, "Openserve order not found.")
            : Result<OpenserveOrderDto>.Success(dto);
    }

    public async Task<Result<OpenserveOrderDto>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.OpenserveOrders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);
        if (entity is null) return Result<OpenserveOrderDto>.Failure(ErrorCodes.NOT_FOUND, "No Openserve order exists for this order.");
        var dto = await MapToDtoAsync(entity.Id, cancellationToken);
        return dto is null
            ? Result<OpenserveOrderDto>.Failure(ErrorCodes.NOT_FOUND, "Openserve order not found.")
            : Result<OpenserveOrderDto>.Success(dto);
    }

    public async Task<Result<PagedResult<OpenserveOrderDto>>> SearchAdminAsync(OpenserveOrderFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new OpenserveOrderFilterRequestDto();
            var query = _dbContext.OpenserveOrders
                .AsNoTracking()
                .Include(o => o.Order)
                .AsQueryable();

            if (filter.Status.HasValue) query = query.Where(o => o.NormalizedStatus == filter.Status.Value);
            if (filter.IsTerminal.HasValue) query = query.Where(o => o.IsTerminal == filter.IsTerminal.Value);
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var s = filter.Search.Trim();
                query = query.Where(o =>
                    EF.Functions.Like(o.ExternalReferenceNumber, $"%{s}%") ||
                    (o.OpenserveOrderId != null && EF.Functions.Like(o.OpenserveOrderId, $"%{s}%")) ||
                    (o.Order != null && EF.Functions.Like(o.Order.OrderNumber, $"%{s}%")));
            }

            var totalCount = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderByDescending(o => o.CreatedAtUtc)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .ToListAsync(cancellationToken);

            var dtos = new List<OpenserveOrderDto>(items.Count);
            foreach (var item in items) dtos.Add(await BuildDtoAsync(item, cancellationToken));

            return Result<PagedResult<OpenserveOrderDto>>.Success(
                new PagedResult<OpenserveOrderDto>(dtos, filter.Page, filter.PageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching Openserve orders.");
            return Result<PagedResult<OpenserveOrderDto>>.Failure(ErrorCodes.EXCEPTION, "Could not search Openserve orders.");
        }
    }

    public async Task<Result<IReadOnlyList<OpenserveOrderStatusHistoryDto>>> GetHistoryAsync(Guid openserveOrderId, CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.OpenserveOrderStatusHistories
            .AsNoTracking()
            .Where(h => h.OpenserveOrderId == openserveOrderId)
            .OrderByDescending(h => h.ReceivedAtUtc)
            .Select(h => new OpenserveOrderStatusHistoryDto
            {
                Id = h.Id,
                OpenserveOrderId = h.OpenserveOrderId,
                OrderId = h.OrderId,
                OpenserveEventId = h.OpenserveEventId,
                CorrelationId = h.CorrelationId,
                EventType = h.EventType.ToString(),
                PreviousRawState = h.PreviousRawState,
                NewRawState = h.NewRawState,
                NormalizedStatus = h.NormalizedStatus.ToString(),
                Description = h.Description,
                InstallationStatus = h.InstallationStatus,
                AppointmentAtUtc = h.AppointmentAtUtc,
                ReceivedAtUtc = h.ReceivedAtUtc,
                EventOccurredAtUtc = h.EventOccurredAtUtc,
                IntegrationLogId = h.IntegrationLogId,
                ProcessingResult = h.ProcessingResult,
                NotificationTriggered = h.NotificationTriggered
            })
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<OpenserveOrderStatusHistoryDto>>.Success(items);
    }

    public async Task<Result<IReadOnlyList<OpenserveIntegrationLogDto>>> GetIntegrationLogsAsync(Guid openserveOrderId, CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.OpenserveIntegrationLogs
            .AsNoTracking()
            .Where(l => l.OpenserveOrderId == openserveOrderId)
            .OrderByDescending(l => l.OccurredAtUtc)
            .Select(l => new OpenserveIntegrationLogDto
            {
                Id = l.Id,
                OpenserveOrderId = l.OpenserveOrderId,
                OpenserveOrderExternalReferenceNumber = l.OpenserveOrder != null ? l.OpenserveOrder.ExternalReferenceNumber : null,
                Direction = l.Direction.ToString(),
                OperationType = l.OperationType.ToString(),
                MessageId = l.MessageId,
                CorrelationId = l.CorrelationId,
                HttpMethod = l.HttpMethod,
                Endpoint = l.Endpoint,
                RequestHeadersJson = l.RequestHeadersJson,
                RequestBodyJson = l.RequestBodyJson,
                ResponseStatusCode = l.ResponseStatusCode,
                ResponseBodyJson = l.ResponseBodyJson,
                OccurredAtUtc = l.OccurredAtUtc,
                IsSuccess = l.IsSuccess,
                ErrorSummary = l.ErrorSummary
            })
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<OpenserveIntegrationLogDto>>.Success(items);
    }

    // ─── internals ──────────────────────────────────────────────────

    private async Task ExecuteSubmissionCallAsync(
        OpenserveOrder openserveOrder, Order order, PackageOpenserveMapping mapping, NetworkAccount networkAccount,
        CancellationToken cancellationToken)
    {
        var settings = _configProvider.Current;

        var command = new OpenserveCreateOrderCommand
        {
            ExternalReferenceNumber = openserveOrder.ExternalReferenceNumber,
            OpenserveProductName = mapping.OpenserveProductName,
            Sku = mapping.Sku,
            Capacity = mapping.Capacity,
            CapacityUom = mapping.CapacityUom,
            SubscriberReferenceNumber = networkAccount.OpenserveSubscriberReferenceNumber,
            SubscriberContactName = order.FullName,
            SubscriberContactPhone = order.PhoneNumber,
            IspIdentifier = settings.IspIdentifier,
            Street1 = order.AddressLine1,
            Suburb = order.Suburb,
            City = order.City,
            Region = order.Province,
            Longitude = order.Longitude?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Latitude = order.Latitude?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Amid = order.OpenserveAmId!,
            Comment = $"SmartFuture order {order.OrderNumber}."
        };

        var result = await _client.CreateOrderAsync(command, cancellationToken);

        _dbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(),
            OpenserveOrderId = openserveOrder.Id,
            Direction = OpenserveIntegrationDirection.Outbound,
            OperationType = OpenserveOperationType.CreateOrder,
            MessageId = result.MessageId,
            HttpMethod = result.HttpMethod,
            Endpoint = result.Endpoint,
            RequestBodyJson = result.RequestBodyJson,
            ResponseStatusCode = result.HttpStatusCode,
            ResponseBodyJson = result.ResponseBodyJson,
            OccurredAtUtc = DateTime.UtcNow,
            IsSuccess = result.IsSuccess,
            ErrorSummary = result.IsSuccess ? null : Truncate($"{result.ErrorCode}: {result.ErrorMessage}", 2000)
        });

        openserveOrder.LastMessageId = result.MessageId;
        openserveOrder.RetryCount += 1;
        openserveOrder.UpdatedAtUtc = DateTime.UtcNow;

        if (result.IsSuccess)
        {
            openserveOrder.NormalizedStatus = OpenserveProvisioningStatus.Submitted;
            openserveOrder.RawState = result.Outcome?.ParsedState ?? "Validated";
            openserveOrder.OpenserveOrderId ??= result.Outcome?.ParsedOrderId;
            openserveOrder.SubmittedAtUtc = DateTime.UtcNow;
            openserveOrder.LastOpenserveUpdateAtUtc = DateTime.UtcNow;
            openserveOrder.LastFailureCode = null;
            openserveOrder.LastFailureMessage = null;

            await _dbContext.SaveChangesAsync(cancellationToken);

            _dbContext.OpenserveOrderStatusHistories.Add(new OpenserveOrderStatusHistory
            {
                Id = Guid.NewGuid(),
                OpenserveOrderId = openserveOrder.Id,
                OrderId = order.Id,
                EventType = OpenserveEventType.Unknown,
                PreviousRawState = null,
                NewRawState = openserveOrder.RawState,
                NormalizedStatus = OpenserveProvisioningStatus.Submitted,
                Description = "Order submitted to Openserve; sync acknowledgement received.",
                ReceivedAtUtc = DateTime.UtcNow,
                EventOccurredAtUtc = DateTime.UtcNow,
                ProcessingResult = "Applied",
                CreatedAtUtc = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(AuditActionType.OpenserveOrderSubmitted, openserveOrder, order,
                $"Order {order.OrderNumber} submitted to Openserve (ExternalReferenceNumber={openserveOrder.ExternalReferenceNumber}).");
        }
        else
        {
            openserveOrder.NormalizedStatus = OpenserveProvisioningStatus.Failed;
            openserveOrder.LastFailureCode = Truncate(result.ErrorCode, 120);
            openserveOrder.LastFailureMessage = Truncate(result.ErrorMessage, 2000);

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(AuditActionType.OpenserveOrderSubmissionFailed, openserveOrder, order,
                $"Openserve submission failed for order {order.OrderNumber}: {result.ErrorCode} {result.ErrorMessage}");
        }
    }

    /// <summary>
    /// Everything that must be true before we're willing to send a real
    /// HTTP request to Openserve. Returns a human-readable reason when
    /// blocked, or null when clear to submit. Deliberately checked both
    /// at first-trigger time AND at retry time (an admin retry might
    /// fix the mapping/AMID and should re-validate, not just resend
    /// stale data).
    /// </summary>
    private static string? DetermineBlockingReason(Order order, PackageOpenserveMapping? mapping)
    {
        if (mapping is null)
            return $"No enabled Openserve package mapping exists for package '{order.PackageName}'. Configure one under Admin > Openserve > Package Mappings.";

        if (string.IsNullOrWhiteSpace(order.OpenserveAmId))
            return "Order has no Openserve AMID (Address Master Identifier). Product Qualification lookup has not been performed for this address.";

        if (string.IsNullOrWhiteSpace(order.AddressLine1))
            return "Order is missing a street address.";

        if (string.IsNullOrWhiteSpace(order.FullName))
            return "Order is missing the customer's full name.";

        if (string.IsNullOrWhiteSpace(order.PhoneNumber) && string.IsNullOrWhiteSpace(order.Email))
            return "Order is missing both a contact phone number and email.";

        return null;
    }

    private async Task<OpenserveOrderDto?> MapToDtoAsync(Guid openserveOrderId, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.OpenserveOrders
            .AsNoTracking()
            .Include(o => o.Order)
            .Include(o => o.PackageOpenserveMapping)
            .FirstOrDefaultAsync(o => o.Id == openserveOrderId, cancellationToken);
        return entity is null ? null : await BuildDtoAsync(entity, cancellationToken);
    }

    private Task<OpenserveOrderDto> BuildDtoAsync(OpenserveOrder o, CancellationToken cancellationToken) => Task.FromResult(new OpenserveOrderDto
    {
        Id = o.Id,
        OrderId = o.OrderId,
        OrderNumber = o.Order?.OrderNumber,
        CustomerName = o.Order?.FullName,
        PackageName = o.Order?.PackageName,
        ExternalReferenceNumber = o.ExternalReferenceNumber,
        SubscriberReferenceNumber = o.SubscriberReferenceNumber,
        OpenserveOrderId = o.OpenserveOrderId,
        OpenserveOrderName = o.OpenserveOrderName,
        OrderType = o.OrderType,
        Reason = o.Reason,
        RawState = o.RawState,
        NormalizedStatus = o.NormalizedStatus.ToString(),
        IsTerminal = o.IsTerminal,
        Sku = o.PackageOpenserveMapping?.Sku,
        Capacity = o.PackageOpenserveMapping?.Capacity,
        CapacityUom = o.PackageOpenserveMapping?.CapacityUom,
        SubmittedAtUtc = o.SubmittedAtUtc,
        LastOpenserveUpdateAtUtc = o.LastOpenserveUpdateAtUtc,
        LastSuccessfulSyncAtUtc = o.LastSuccessfulSyncAtUtc,
        RetryCount = o.RetryCount,
        LastFailureCode = o.LastFailureCode,
        LastFailureMessage = o.LastFailureMessage,
        CreatedAtUtc = o.CreatedAtUtc,
        UpdatedAtUtc = o.UpdatedAtUtc
    });

    private async Task EmitAuditAsync(AuditActionType actionType, OpenserveOrder openserveOrder, Order order, string summary)
    {
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = _currentUser.UserId.HasValue ? AuditActorType.Admin : AuditActorType.System,
                ActionType = actionType,
                EntityType = AuditEntityType.OpenserveOrder,
                EntityId = openserveOrder.Id,
                EntityName = order.OrderNumber,
                Summary = summary,
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = actionType != AuditActionType.OpenserveOrderSubmissionFailed
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Openserve audit log write failed for OpenserveOrder {Id}.", openserveOrder.Id);
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}

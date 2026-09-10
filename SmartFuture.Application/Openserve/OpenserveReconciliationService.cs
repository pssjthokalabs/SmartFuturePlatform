using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

public class OpenserveReconciliationService : IOpenserveReconciliationService
{
    private readonly IAppDbContext _dbContext;
    private readonly IOpenserveApiClient _client;
    private readonly IOpenserveOrderUpdatePipeline _pipeline;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<OpenserveReconciliationService> _logger;

    public OpenserveReconciliationService(
        IAppDbContext dbContext, IOpenserveApiClient client, IOpenserveOrderUpdatePipeline pipeline,
        IOpenserveRuntimeConfigProvider configProvider, IAuditService auditService, ICurrentUserService currentUser,
        ILogger<OpenserveReconciliationService> logger)
    {
        _dbContext = dbContext;
        _client = client;
        _pipeline = pipeline;
        _configProvider = configProvider;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result> SynchronizeNowAsync(Guid openserveOrderId, bool isManualTrigger = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_configProvider.Current.Enabled)
                return Result.Failure(ErrorCodes.VALIDATION_ERROR, "OpenserveFulfilment integration is disabled.");

            var openserveOrder = await _dbContext.OpenserveOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == openserveOrderId, cancellationToken);
            if (openserveOrder is null) return Result.Failure(ErrorCodes.NOT_FOUND, "Openserve order not found.");

            if (string.IsNullOrWhiteSpace(openserveOrder.OpenserveOrderId))
                return Result.Failure(ErrorCodes.VALIDATION_ERROR, "Openserve order id is not yet known for this order (submission hasn't been acknowledged) — nothing to GET.");

            var apiResult = await _client.GetOrderAsync(openserveOrder.OpenserveOrderId, cancellationToken);

            var log = new OpenserveIntegrationLog
            {
                Id = Guid.NewGuid(),
                OpenserveOrderId = openserveOrder.Id,
                Direction = OpenserveIntegrationDirection.Outbound,
                OperationType = OpenserveOperationType.GetOrder,
                MessageId = apiResult.MessageId,
                HttpMethod = apiResult.HttpMethod,
                Endpoint = apiResult.Endpoint,
                ResponseStatusCode = apiResult.HttpStatusCode,
                ResponseBodyJson = apiResult.ResponseBodyJson,
                OccurredAtUtc = DateTime.UtcNow,
                IsSuccess = apiResult.IsSuccess,
                ErrorSummary = apiResult.IsSuccess ? null : $"{apiResult.ErrorCode}: {apiResult.ErrorMessage}"
            };
            _dbContext.OpenserveIntegrationLogs.Add(log);
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (isManualTrigger)
            {
                await EmitAuditAsync(
                    AuditActionType.OpenserveManualSynchronize, openserveOrder.Id, openserveOrder.ExternalReferenceNumber,
                    $"Admin synchronized Openserve order {openserveOrder.OpenserveOrderId ?? openserveOrder.ExternalReferenceNumber}.",
                    apiResult.IsSuccess);
            }

            if (!apiResult.IsSuccess || apiResult.Outcome is null)
            {
                _logger.LogWarning(
                    "[Openserve][reconcile] GET failed for order {OpenserveOrderId}: {Code} {Message}",
                    openserveOrder.OpenserveOrderId, apiResult.ErrorCode, apiResult.ErrorMessage);
                return Result.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, apiResult.ErrorMessage ?? "GET Product Order failed.");
            }

            if (string.IsNullOrWhiteSpace(apiResult.Outcome.State))
            {
                return Result.Success("GET succeeded but returned no state — nothing to apply.");
            }

            var outcome = await _pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
            {
                OpenserveOrderId = openserveOrder.OpenserveOrderId,
                RawState = apiResult.Outcome.State,
                OrderName = apiResult.Outcome.OrderName,
                EventType = OpenserveEventType.Unknown,
                IntegrationLogId = log.Id,
                IsReconciliation = true
            }, cancellationToken);

            return Result.Success($"Reconciliation applied: {outcome.Kind}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reconciling Openserve order {Id}.", openserveOrderId);
            return Result.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred during reconciliation.");
        }
    }

    public async Task<Result> AdminCancelOrderAsync(Guid openserveOrderId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_configProvider.Current.Enabled)
                return Result.Failure(ErrorCodes.VALIDATION_ERROR, "OpenserveFulfilment integration is disabled.");

            var openserveOrder = await _dbContext.OpenserveOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == openserveOrderId, cancellationToken);
            if (openserveOrder is null) return Result.Failure(ErrorCodes.NOT_FOUND, "Openserve order not found.");

            if (openserveOrder.IsTerminal)
                return Result.Failure(ErrorCodes.CONFLICT, $"Order is already in a terminal state ({openserveOrder.NormalizedStatus}) and cannot be cancelled.");

            if (string.IsNullOrWhiteSpace(openserveOrder.OpenserveOrderId))
                return Result.Failure(ErrorCodes.VALIDATION_ERROR, "Openserve order id is not yet known — cannot cancel a submission that hasn't been acknowledged.");

            var apiResult = await _client.CancelOrderAsync(openserveOrder.OpenserveOrderId, cancellationToken);

            var log = new OpenserveIntegrationLog
            {
                Id = Guid.NewGuid(),
                OpenserveOrderId = openserveOrder.Id,
                Direction = OpenserveIntegrationDirection.Outbound,
                OperationType = OpenserveOperationType.CancelOrder,
                MessageId = apiResult.MessageId,
                HttpMethod = apiResult.HttpMethod,
                Endpoint = apiResult.Endpoint,
                RequestBodyJson = apiResult.RequestBodyJson,
                ResponseStatusCode = apiResult.HttpStatusCode,
                ResponseBodyJson = apiResult.ResponseBodyJson,
                OccurredAtUtc = DateTime.UtcNow,
                IsSuccess = apiResult.IsSuccess,
                ErrorSummary = apiResult.IsSuccess ? null : $"{apiResult.ErrorCode}: {apiResult.ErrorMessage}"
            };
            _dbContext.OpenserveIntegrationLogs.Add(log);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.OpenserveManualCancel, openserveOrder.Id, openserveOrder.ExternalReferenceNumber,
                $"Admin cancelled Openserve order {openserveOrder.OpenserveOrderId ?? openserveOrder.ExternalReferenceNumber}.",
                apiResult.IsSuccess);

            if (!apiResult.IsSuccess || apiResult.Outcome is null)
                return Result.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, apiResult.ErrorMessage ?? "Cancel Product Order failed.");

            var outcome = await _pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
            {
                OpenserveOrderId = openserveOrder.OpenserveOrderId,
                RawState = apiResult.Outcome.State ?? "Cancelled",
                Description = "Cancellation requested by admin.",
                EventType = OpenserveEventType.Unknown,
                IntegrationLogId = log.Id,
                IsReconciliation = false
            }, cancellationToken);

            return Result.Success($"Cancellation submitted: {outcome.Kind}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error cancelling Openserve order {Id}.", openserveOrderId);
            return Result.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while cancelling the order.");
        }
    }

    public async Task<int> ReconcileNonTerminalOrdersAsync(CancellationToken cancellationToken = default)
    {
        if (!_configProvider.Current.Enabled) return 0;

        var candidates = await _dbContext.OpenserveOrders
            .AsNoTracking()
            .Where(o => !o.IsTerminal && o.OpenserveOrderId != null)
            .OrderBy(o => o.LastSuccessfulSyncAtUtc ?? DateTime.MinValue) // longest-unchecked first
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var id in candidates)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var result = await SynchronizeNowAsync(id, isManualTrigger: false, cancellationToken);
                if (!result.IsSuccess)
                {
                    _logger.LogWarning("[Openserve][reconcile] tick failed for OpenserveOrder {Id}: {Code} {Message}", id, result.Code, result.Message);
                }
                processed++;
            }
            catch (Exception ex)
            {
                // One order's failure must never stop the rest —
                // mirrors the job-crawler's per-source isolation.
                _logger.LogError(ex, "[Openserve][reconcile] tick threw for OpenserveOrder {Id}.", id);
            }
        }

        return processed;
    }

    private async Task EmitAuditAsync(AuditActionType actionType, Guid openserveOrderId, string entityName, string summary, bool isSuccess)
    {
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = AuditActorType.Admin,
                ActionType = actionType,
                EntityType = AuditEntityType.OpenserveOrder,
                EntityId = openserveOrderId,
                EntityName = entityName,
                Summary = summary,
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = isSuccess
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Openserve manual-action audit log write failed for OpenserveOrder {Id}.", openserveOrderId);
        }
    }
}

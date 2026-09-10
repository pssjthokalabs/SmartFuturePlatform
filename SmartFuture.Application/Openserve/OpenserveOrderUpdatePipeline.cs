using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// The single shared Openserve status-update handler (brief Priority
/// 3). OpenserveCallbackController, OpenserveEventController, and
/// OpenserveReconciliationService all call ApplyUpdateAsync — none of
/// them contain their own business logic for what a state change
/// means. This is deliberate: duplicate-event idempotency, out-of-order
/// handling, history, notifications, and auto-activation only need to
/// be correct in ONE place.
///
/// Correlation note: neither the async callback payload nor the event
/// notification payload carries our ExternalReferenceNumber anywhere
/// in the documented examples — the ONLY correlation key both sides
/// share is Openserve's own numeric order id. We learn that id early
/// (parsed best-effort from the synchronous CREATE acknowledgement's
/// free-text message — see OpenserveOrderSubmissionService), which is
/// what makes matching by OpenserveOrderId reliable here. If that
/// parse ever fails for a given order, this pipeline correctly reports
/// UnknownOrder rather than guessing — flagged as an open item in the
/// project report.
/// </summary>
public class OpenserveOrderUpdatePipeline : IOpenserveOrderUpdatePipeline
{
    private readonly IAppDbContext _dbContext;
    private readonly IOrderService _orderService;
    private readonly IOpenserveCustomerNotificationService _notifications;
    private readonly IAuditService _auditService;
    private readonly ILogger<OpenserveOrderUpdatePipeline> _logger;

    public OpenserveOrderUpdatePipeline(
        IAppDbContext dbContext, IOrderService orderService, IOpenserveCustomerNotificationService notifications,
        IAuditService auditService, ILogger<OpenserveOrderUpdatePipeline> logger)
    {
        _dbContext = dbContext;
        _orderService = orderService;
        _notifications = notifications;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<OpenserveUpdateOutcome> ApplyUpdateAsync(OpenserveUpdateInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            OpenserveOrder? openserveOrder = null;
            if (!string.IsNullOrWhiteSpace(input.OpenserveOrderId))
            {
                openserveOrder = await _dbContext.OpenserveOrders
                    .FirstOrDefaultAsync(o => o.OpenserveOrderId == input.OpenserveOrderId, cancellationToken);
            }

            if (openserveOrder is null)
            {
                _logger.LogWarning(
                    "[Openserve] Update could not be correlated to any known order. openserveOrderId={OpenserveOrderId} eventType={EventType} eventId={EventId}",
                    input.OpenserveOrderId, input.EventType, input.OpenserveEventId);
                return new OpenserveUpdateOutcome { Kind = OpenserveUpdateResultKind.UnknownOrder, Message = "No matching OpenserveOrder found." };
            }

            // Duplicate: same event id already processed for this order.
            if (!string.IsNullOrWhiteSpace(input.OpenserveEventId))
            {
                var alreadyProcessed = await _dbContext.OpenserveOrderStatusHistories.AnyAsync(
                    h => h.OpenserveOrderId == openserveOrder.Id && h.OpenserveEventId == input.OpenserveEventId,
                    cancellationToken);
                if (alreadyProcessed)
                {
                    return new OpenserveUpdateOutcome
                    {
                        Kind = OpenserveUpdateResultKind.Duplicate,
                        OpenserveOrderId = openserveOrder.Id,
                        Message = "Event already processed."
                    };
                }
            }

            // No-change: reconciliation re-confirming a state we already
            // have on file. Bump LastSuccessfulSyncAtUtc (proves we
            // checked) but write no history row — otherwise a 30-minute
            // poll would spam identical rows forever.
            var rawStateUnchanged = string.Equals(
                (input.RawState ?? string.Empty).Trim(), (openserveOrder.RawState ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
            if (input.IsReconciliation && rawStateUnchanged && string.IsNullOrWhiteSpace(input.OpenserveEventId))
            {
                openserveOrder.LastSuccessfulSyncAtUtc = DateTime.UtcNow;
                openserveOrder.UpdatedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
                return new OpenserveUpdateOutcome
                {
                    Kind = OpenserveUpdateResultKind.NoChange,
                    OpenserveOrderId = openserveOrder.Id,
                    Message = "State unchanged."
                };
            }

            var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == openserveOrder.OrderId, cancellationToken);
            if (order is null)
            {
                _logger.LogError("[Openserve] OpenserveOrder {Id} references missing Order {OrderId}.", openserveOrder.Id, openserveOrder.OrderId);
                return new OpenserveUpdateOutcome { Kind = OpenserveUpdateResultKind.Error, OpenserveOrderId = openserveOrder.Id, Message = "Linked Order not found." };
            }

            var previousRawState = openserveOrder.RawState;
            var previousNormalized = openserveOrder.NormalizedStatus;
            var newNormalized = OpenserveStatusNormalizer.Normalize(input.RawState);

            // Out-of-order: an update that claims to have occurred
            // before the last one we already applied. Still recorded
            // for audit completeness; current fields are NOT regressed.
            var isOutOfOrder = input.EventOccurredAtUtc.HasValue
                && openserveOrder.LastOpenserveUpdateAtUtc.HasValue
                && input.EventOccurredAtUtc.Value < openserveOrder.LastOpenserveUpdateAtUtc.Value;

            var now = DateTime.UtcNow;
            var processingResult = isOutOfOrder ? "IgnoredOutOfOrder" : "Applied";

            if (!isOutOfOrder)
            {
                openserveOrder.RawState = input.RawState;
                openserveOrder.NormalizedStatus = newNormalized;
                if (!string.IsNullOrWhiteSpace(input.OrderName)) openserveOrder.OpenserveOrderName = input.OrderName;
                openserveOrder.LastOpenserveUpdateAtUtc = input.EventOccurredAtUtc ?? now;
                openserveOrder.CorrelationId = input.CorrelationId ?? openserveOrder.CorrelationId;
                openserveOrder.IsTerminal = OpenserveStatusNormalizer.IsTerminal(newNormalized);
            }
            openserveOrder.LastSuccessfulSyncAtUtc = now;
            openserveOrder.UpdatedAtUtc = now;

            var notificationTriggered = false;
            if (!isOutOfOrder && newNormalized != previousNormalized)
            {
                notificationTriggered = await _notifications.NotifyStatusChangedAsync(
                    openserveOrder, order, previousNormalized, newNormalized, cancellationToken);
            }

            _dbContext.OpenserveOrderStatusHistories.Add(new OpenserveOrderStatusHistory
            {
                Id = Guid.NewGuid(),
                OpenserveOrderId = openserveOrder.Id,
                OrderId = openserveOrder.OrderId,
                OpenserveEventId = input.OpenserveEventId,
                CorrelationId = input.CorrelationId,
                EventType = input.EventType,
                PreviousRawState = previousRawState,
                NewRawState = input.RawState,
                NormalizedStatus = newNormalized,
                Description = input.Description,
                ReceivedAtUtc = now,
                EventOccurredAtUtc = input.EventOccurredAtUtc,
                IntegrationLogId = input.IntegrationLogId,
                ProcessingResult = processingResult,
                NotificationTriggered = notificationTriggered,
                CreatedAtUtc = now
            });

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(openserveOrder, order, previousNormalized, newNormalized, isOutOfOrder);

            // Auto-activation (brief Priority 9): only when Openserve
            // reaches its own terminal Accepted/Completed state, and
            // only forward-applied (never from an out-of-order replay).
            if (!isOutOfOrder && newNormalized == OpenserveProvisioningStatus.Completed && previousNormalized != OpenserveProvisioningStatus.Completed)
            {
                await TryAutoActivateAsync(openserveOrder, order, cancellationToken);
            }

            return new OpenserveUpdateOutcome
            {
                Kind = isOutOfOrder ? OpenserveUpdateResultKind.AppliedOutOfOrder : OpenserveUpdateResultKind.Applied,
                OpenserveOrderId = openserveOrder.Id,
                Message = processingResult
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error applying Openserve update. openserveOrderId={OpenserveOrderId}", input.OpenserveOrderId);
            return new OpenserveUpdateOutcome { Kind = OpenserveUpdateResultKind.Error, Message = "Unexpected error." };
        }
    }

    private async Task TryAutoActivateAsync(OpenserveOrder openserveOrder, Domain.Orders.Order order, CancellationToken cancellationToken)
    {
        try
        {
            var reference = openserveOrder.OpenserveOrderName ?? openserveOrder.OpenserveOrderId ?? openserveOrder.ExternalReferenceNumber;
            var result = await _orderService.TryOpenserveConfirmedActivateServiceAsync(order.Id, reference, cancellationToken);
            if (result.IsSuccess && result.Message == IOrderService.OpenserveActivationSuccessMessage)
            {
                await _notifications.NotifyServiceActivatedAsync(order, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Openserve auto-activation hook threw for order {OrderNumber}.", order.OrderNumber);
        }
    }

    private async Task EmitAuditAsync(
        OpenserveOrder openserveOrder, Domain.Orders.Order order,
        OpenserveProvisioningStatus previous, OpenserveProvisioningStatus current, bool isOutOfOrder)
    {
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorType = AuditActorType.System,
                ActionType = AuditActionType.OpenserveOrderStatusChanged,
                EntityType = AuditEntityType.OpenserveOrder,
                EntityId = openserveOrder.Id,
                EntityName = order.OrderNumber,
                Summary = isOutOfOrder
                    ? $"Openserve out-of-order update received for order {order.OrderNumber} (raw state kept at {openserveOrder.RawState}); recorded in history only."
                    : $"Openserve status changed: {previous} -> {current} (order {order.OrderNumber})",
                IsSuccess = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Openserve status-change audit log write failed for OpenserveOrder {Id}.", openserveOrder.Id);
        }
    }
}

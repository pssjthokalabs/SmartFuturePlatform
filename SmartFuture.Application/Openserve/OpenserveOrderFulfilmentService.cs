using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

public interface IOpenserveOrderFulfilmentService
{
    Task<Result<OpenserveOrderFulfilmentDto>> GetAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Admin Send / Retry from Order Detail — the same coordinator as every other path.</summary>
    Task<Result<OpenserveOrderFulfilmentDto>> SubmitAsync(Guid orderId, bool confirmOutcomeUnknown, CancellationToken cancellationToken = default);

    Task<Result<OpenserveOrderFulfilmentDto>> PauseAutomationAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default);

    Task<Result<OpenserveOrderFulfilmentDto>> ResumeAutomationAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// Admin Order Detail → "OPENserve Fulfilment". Read model + the per-order
/// Pause/Resume control. Eligibility shown here is computed with the same
/// <see cref="OpenserveSubmissionRules"/> the coordinator enforces, and every
/// action is re-validated server-side, so the UI never offers what the API
/// would refuse. Admin-only — nothing here is used by customer endpoints.
/// </summary>
public class OpenserveOrderFulfilmentService : IOpenserveOrderFulfilmentService
{
    private const int MaxActivityItems = 60;
    private const int MaxPauseReasonLength = 500;

    private readonly IAppDbContext _dbContext;
    private readonly IOpenserveOrderSubmissionService _submission;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<OpenserveOrderFulfilmentService> _logger;

    public OpenserveOrderFulfilmentService(IAppDbContext dbContext, IOpenserveOrderSubmissionService submission, IOpenserveRuntimeConfigProvider configProvider, IAuditService auditService,
        ICurrentUserService currentUser, ILogger<OpenserveOrderFulfilmentService> logger)
    {
        _dbContext = dbContext;
        _submission = submission;
        _configProvider = configProvider;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> GetAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = await BuildAsync(orderId, cancellationToken);
            return dto is null
                ? Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.")
                : Result<OpenserveOrderFulfilmentDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading Openserve fulfilment for order {OrderId}.", orderId);
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.EXCEPTION, "Could not load the Openserve fulfilment state.");
        }
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> SubmitAsync(Guid orderId, bool confirmOutcomeUnknown, CancellationToken cancellationToken = default)
    {
        var attempt = await _submission.SubmitAsync(new OpenserveSubmissionRequest(orderId, OpenserveSubmissionTrigger.AdminManual) { ConfirmOutcomeUnknown = confirmOutcomeUnknown }, cancellationToken);
        if (!attempt.IsSuccess) return Result<OpenserveOrderFulfilmentDto>.Failure(attempt.Code ?? ErrorCodes.VALIDATION_ERROR, attempt.Message);

        var dto = await BuildAsync(orderId, cancellationToken);
        return dto is null
            ? Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.")
            : Result<OpenserveOrderFulfilmentDto>.Success(dto, attempt.Message);
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> PauseAutomationAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        if (order.PackageType != ServicePackageType.Fibre) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Openserve automation only applies to Fibre orders.");
        if (order.OpenserveAutomationPaused) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.CONFLICT, "Openserve automation is already paused for this order.");

        var record = await FindSalesOrderAsync(orderId, cancellationToken);
        if (record is not null && OpenserveSubmissionRules.IsForwarded(record))
            return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "This order is already with Openserve — there is no submission left to pause. Status tracking continues.");

        var trimmed = Clean(reason);
        order.OpenserveAutomationPaused = true;
        order.OpenserveAutomationPausedAtUtc = DateTime.UtcNow;
        order.OpenserveAutomationPausedByUserId = _currentUser.UserId;
        order.OpenserveAutomationPauseReason = trimmed;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await AuditAsync(AuditActionType.OpenserveAutomationPaused, order, $"Openserve automation paused for order {order.OrderNumber}{(trimmed is null ? "." : $": {trimmed}")}", trimmed);
        return await GetAsync(orderId, cancellationToken);
    }

    public async Task<Result<OpenserveOrderFulfilmentDto>> ResumeAutomationAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");
        if (!order.OpenserveAutomationPaused) return Result<OpenserveOrderFulfilmentDto>.Failure(ErrorCodes.CONFLICT, "Openserve automation is not paused for this order.");

        var trimmed = Clean(reason);
        order.OpenserveAutomationPaused = false;
        order.OpenserveAutomationPausedAtUtc = null;
        order.OpenserveAutomationPausedByUserId = null;
        order.OpenserveAutomationPauseReason = null;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await AuditAsync(AuditActionType.OpenserveAutomationResumed, order, $"Openserve automation resumed for order {order.OrderNumber}{(trimmed is null ? "." : $": {trimmed}")}", trimmed);
        return await GetAsync(orderId, cancellationToken);
    }

    // ─── read model ─────────────────────────────────────────────────

    private async Task<OpenserveOrderFulfilmentDto?> BuildAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _dbContext.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null) return null;

        var settings = _configProvider.Current;
        var recovery = settings.SubmissionRecovery;
        var now = DateTime.UtcNow;

        var dto = new OpenserveOrderFulfilmentDto
        {
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            PackageName = order.PackageName,
            OrderStatus = order.Status.ToString(),
            InstallationAddress = FormatAddress(order),
            AppliesToOrder = order.PackageType == ServicePackageType.Fibre,
            IntegrationEnabled = settings.Enabled,
            AmId = order.OpenserveAmId,
            BuildingNumId = order.OpenserveBuildingNumId,
            MaxAutomaticRetries = recovery.MaxAttempts
        };

        if (!dto.AppliesToOrder)
        {
            dto.State = OpenserveFulfilmentState.NotApplicable;
            dto.StateLabel = "NOT APPLICABLE";
            dto.StateReason = "Only Fibre orders are fulfilled through Openserve.";
            dto.ManualSubmission = new OpenserveManualSubmissionDto { Allowed = false, Reason = dto.StateReason };
            dto.AutomaticRetry = new OpenserveFulfilmentPermissionDto { Allowed = false, Reason = dto.StateReason };
            return dto;
        }

        var record = await FindSalesOrderAsync(order.Id, cancellationToken);
        var accounts = await _dbContext.NetworkAccounts.AsNoTracking().Where(n => n.OrderId == order.Id).ToListAsync(cancellationToken);
        var account = OpenserveSubmissionRules.PickNetworkAccount(accounts);
        var currentMapping = order.ServicePackageId is null
            ? null
            : await _dbContext.PackageOpenserveMappings.AsNoTracking().FirstOrDefaultAsync(m => m.ServicePackageId == order.ServicePackageId && m.IsEnabled, cancellationToken);
        var recordMapping = record?.PackageOpenserveMappingId is { } mappingId
            ? await _dbContext.PackageOpenserveMappings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mappingId, cancellationToken)
            : null;

        var forwarded = record is not null && OpenserveSubmissionRules.IsForwarded(record);
        var shownMapping = forwarded ? recordMapping ?? currentMapping : currentMapping;
        dto.ProductName = shownMapping?.OpenserveProductName;
        dto.Sku = shownMapping?.Sku;
        dto.Capacity = shownMapping?.Capacity;
        dto.CapacityUom = shownMapping?.CapacityUom;
        dto.HasEnabledMapping = currentMapping is not null;
        dto.SubscriberReferenceNumber = record?.SubscriberReferenceNumber ?? account?.OpenserveSubscriberReferenceNumber;
        dto.ForwardedToOpenserve = forwarded;

        if (record is not null)
        {
            dto.OpenserveOrderRecordId = record.Id;
            dto.ExternalReferenceNumber = record.ExternalReferenceNumber;
            dto.OpenserveOrderId = record.OpenserveOrderId;
            dto.OpenserveOrderName = record.OpenserveOrderName;
            dto.NormalizedStatus = record.NormalizedStatus.ToString();
            dto.RawState = record.RawState;
            dto.IsTerminal = record.IsTerminal;
            dto.LastSubmissionAttemptAtUtc = record.LastSubmissionAttemptAtUtc ?? (record.RetryCount > 0 ? record.UpdatedAtUtc : null);
            dto.LastSubmissionTrigger = record.LastSubmissionTrigger?.ToString();
            dto.SubmittedAtUtc = record.SubmittedAtUtc;
            dto.LastSuccessfulSyncAtUtc = record.LastSuccessfulSyncAtUtc;
            dto.LastOpenserveUpdateAtUtc = record.LastOpenserveUpdateAtUtc;
            dto.AttemptCount = record.RetryCount;
            dto.AutomaticRetryCount = record.AutomaticRetryCount;
            dto.LastFailureClass = record.LastFailureClass.ToString();
            dto.LastFailureCode = record.LastFailureCode;
            dto.LastFailureMessage = record.LastFailureMessage;
            dto.FailureClassExplanation = record.LastFailureClass == OpenserveSubmissionFailureClass.None ? null : OpenserveSubmissionFailureClassifier.ExplainClass(record.LastFailureClass);
            dto.NextAutomaticRetryAtUtc = record.NextAutomaticRetryAtUtc;
        }

        var staleClaim = record is not null && OpenserveSubmissionRules.IsStaleSubmitting(record, now, recovery.StaleSubmissionMinutes);
        var blocker = OpenserveSubmissionRules.PreflightBlocker(order, currentMapping, settings);
        var gate = OpenserveSubmissionRules.OrderGateReason(order);

        ApplyState(dto, order, record, account, blocker, staleClaim, settings);
        dto.ManualSubmission = ManualSubmission(order, record, account, blocker, gate, staleClaim, settings, now);
        dto.AutomaticRetry = await AutomaticRetryAsync(order, record, account, blocker, gate, staleClaim, settings, now, cancellationToken);
        dto.Automation = await AutomationStateAsync(order, forwarded, cancellationToken);
        dto.Activity = await ActivityAsync(order, record, cancellationToken);
        return dto;
    }

    private static void ApplyState(OpenserveOrderFulfilmentDto dto, Order order, OpenserveOrder? record, NetworkAccount? account, (string Code, string Reason)? blocker, bool staleClaim,
        OpenserveFulfilmentSettings settings)
    {
        void Set(string state, string label, string? reason)
        {
            dto.State = state;
            dto.StateLabel = label;
            dto.StateReason = reason;
        }

        if (record is not null && OpenserveSubmissionRules.IsForwarded(record))
        {
            var reference = record.OpenserveOrderName ?? record.OpenserveOrderId ?? record.ExternalReferenceNumber;
            switch (record.NormalizedStatus)
            {
                case OpenserveProvisioningStatus.Completed: Set(OpenserveFulfilmentState.Completed, "COMPLETED", $"Openserve completed order {reference}."); break;
                case OpenserveProvisioningStatus.Cancelled: Set(OpenserveFulfilmentState.Cancelled, "CANCELLED", $"Openserve order {reference} was cancelled."); break;
                case OpenserveProvisioningStatus.InProgress:
                case OpenserveProvisioningStatus.AwaitingCancellation:
                    Set(OpenserveFulfilmentState.InProgress, "IN PROGRESS", $"Openserve is working on order {reference} ({record.RawState ?? record.NormalizedStatus.ToString()})."); break;
                default: Set(OpenserveFulfilmentState.Submitted, "SUBMITTED", $"Openserve accepted order {reference}. Status updates arrive by callback and reconciliation."); break;
            }
            return;
        }

        if (order.Status is Shared.Enums.Orders.OrderStatus.Cancelled or Shared.Enums.Orders.OrderStatus.Rejected or Shared.Enums.Orders.OrderStatus.Failed)
        {
            Set(OpenserveFulfilmentState.OrderCancelled, "ORDER CANCELLED — NOT SENT", OpenserveSubmissionRules.OrderGateReason(order));
            return;
        }
        if (!settings.Enabled)
        {
            Set(OpenserveFulfilmentState.IntegrationDisabled, "OPENSERVE INTEGRATION DISABLED", "Openserve integration is disabled. Nothing is sent to Openserve — automatically or manually — until it is enabled.");
            return;
        }
        if (order.OpenserveAutomationPaused)
        {
            Set(OpenserveFulfilmentState.BlockedAdmin, "BLOCKED — ADMIN", "Openserve automation is paused for this order by an Admin. Nothing will be sent until it is resumed.");
            return;
        }

        if (record is not null)
        {
            if (record.NormalizedStatus == OpenserveProvisioningStatus.Submitting && !staleClaim)
            {
                Set(OpenserveFulfilmentState.SubmissionPending, "SUBMISSION PENDING", "A submission attempt is in progress.");
                return;
            }
            if (staleClaim)
            {
                Set(OpenserveFulfilmentState.FailedOutcomeUnknown, "FAILED — CHECK WITH OPENSERVE", OpenserveSubmissionFailureClassifier.Classify(null, OpenserveApiErrorCodes.Interrupted).Explanation);
                return;
            }

            var message = record.LastFailureMessage;
            switch (record.LastFailureClass)
            {
                case OpenserveSubmissionFailureClass.Retryable:
                    Set(OpenserveFulfilmentState.FailedRetryable, "FAILED — RETRYABLE", message); return;
                case OpenserveSubmissionFailureClass.OutcomeUnknown:
                    Set(OpenserveFulfilmentState.FailedOutcomeUnknown, "FAILED — CHECK WITH OPENSERVE", message); return;
                case OpenserveSubmissionFailureClass.Blocked:
                    var code = OpenserveSubmissionRules.InferBlockedCode(record.LastFailureCode, record.LastFailureMessage);
                    var resolvedNote = blocker is null ? " This now looks resolved — use Retry Openserve Submission to send it." : string.Empty;
                    var (state, label) = BlockedState(code);
                    Set(state, label, message + resolvedNote);
                    return;
                default:
                    Set(OpenserveFulfilmentState.FailedRejected, "FAILED — REJECTED BY OPENSERVE", message); return;
            }
        }

        // No submission record yet.
        if (!OpenserveSubmissionRules.SubmittableOrderStatuses.Contains(order.Status))
        {
            Set(OpenserveFulfilmentState.AwaitingPayment, "NOT SUBMITTED — AWAITING PAYMENT", OpenserveSubmissionRules.OrderGateReason(order));
            return;
        }
        if (account is null)
        {
            Set(OpenserveFulfilmentState.NotSubmitted, "NOT SUBMITTED", "No network account has been reserved for this order yet, so it hasn't reached the Openserve submission point.");
            return;
        }
        if (blocker is { } b)
        {
            var (state, label) = BlockedState(b.Code);
            Set(state, label, b.Reason);
            return;
        }
        Set(OpenserveFulfilmentState.NotSubmitted, "NOT SUBMITTED", "This order has not been sent to Openserve. It was not picked up automatically (for example, the integration was disabled when payment landed).");
    }

    private static (string State, string Label) BlockedState(string code) => code switch
    {
        OpenserveBlockedCodes.Configuration => (OpenserveFulfilmentState.BlockedConfiguration, "BLOCKED — CONFIGURATION"),
        OpenserveBlockedCodes.Mapping => (OpenserveFulfilmentState.BlockedPackageMapping, "BLOCKED — PACKAGE MAPPING"),
        _ => (OpenserveFulfilmentState.BlockedOrderData, "BLOCKED — ORDER DETAILS")
    };

    private static OpenserveManualSubmissionDto ManualSubmission(Order order, OpenserveOrder? record, NetworkAccount? account, (string Code, string Reason)? blocker, string? gate, bool staleClaim,
        OpenserveFulfilmentSettings settings, DateTime now)
    {
        OpenserveManualSubmissionDto No(string reason) => new() { Allowed = false, Reason = reason };

        if (record is not null && OpenserveSubmissionRules.IsForwarded(record)) return No("Already submitted to Openserve — it is never sent twice. Use Synchronize to refresh its status.");
        if (!settings.Enabled) return No("Openserve integration is disabled.");
        if (gate is not null) return No(gate);
        if (account is null) return No("No network account has been reserved for this order yet (payment not applied).");
        if (record is not null && record.NormalizedStatus == OpenserveProvisioningStatus.Submitting && !staleClaim) return No("A submission attempt is already in progress.");
        if (blocker is { } b) return No(b.Reason);

        var requiresConfirmation = staleClaim || record?.LastFailureClass == OpenserveSubmissionFailureClass.OutcomeUnknown;
        return new OpenserveManualSubmissionDto
        {
            Allowed = true,
            Action = record is null ? "Send" : "Retry",
            RequiresOutcomeConfirmation = requiresConfirmation,
            Reason = requiresConfirmation
                ? $"Openserve may already have received this order. Confirm with Openserve that no order exists for reference {record!.ExternalReferenceNumber} before retrying."
                : record is null ? "Ready to send to Openserve." : "Safe to retry — the same SmartFuture order and references are reused."
        };
    }

    private async Task<OpenserveFulfilmentPermissionDto> AutomaticRetryAsync(Order order, OpenserveOrder? record, NetworkAccount? account, (string Code, string Reason)? blocker, string? gate, bool staleClaim,
        OpenserveFulfilmentSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        OpenserveFulfilmentPermissionDto No(string reason) => new() { Allowed = false, Reason = reason };
        var recovery = settings.SubmissionRecovery;

        if (record is not null && OpenserveSubmissionRules.IsForwarded(record)) return No("Not needed — Openserve has the order. Status is tracked by reconciliation (GET), never by resubmitting.");
        if (!settings.Enabled) return No("Openserve integration is disabled.");
        if (!recovery.Enabled) return No("Automatic submission recovery is switched off (OpenserveFulfilment:SubmissionRecovery:Enabled).");
        if (gate is not null) return No(gate);

        if (record is null)
        {
            if (account is null) return No("The order hasn't reached the submission point (no network account reserved).");
            if (blocker is { } b) return No($"Blocked: {b.Reason}");
            if (!OpenserveSubmissionRules.SweepOrderStatuses.Contains(order.Status) || account.Status != NetworkAccountStatus.Pending)
                return No("The safety sweep only picks up orders whose installation hasn't happened yet — send it manually if Openserve still needs it.");

            var (floor, ceiling) = await OpenserveSubmissionRecoveryService.GetSafetySweepWindowAsync(_dbContext, recovery, now, cancellationToken);
            if (account.CreatedAtUtc < floor)
                return No("Outside the safety-sweep window (it became eligible before the integration was last enabled, or too long ago) — send it manually.");
            return new OpenserveFulfilmentPermissionDto
            {
                Allowed = true,
                Reason = account.CreatedAtUtc > ceiling
                    ? "The automatic trigger should handle it; the safety sweep picks it up if not."
                    : $"The safety sweep (every {Math.Clamp(recovery.SafetySweepIntervalHours, 1, 24)}h) will submit it."
            };
        }

        if (record.NormalizedStatus == OpenserveProvisioningStatus.Submitting && !staleClaim) return No("A submission attempt is in progress.");
        if (staleClaim) return No(OpenserveSubmissionFailureClassifier.ExplainClass(OpenserveSubmissionFailureClass.OutcomeUnknown));

        return record.LastFailureClass switch
        {
            OpenserveSubmissionFailureClass.Retryable when record.NextAutomaticRetryAtUtc is not null => new OpenserveFulfilmentPermissionDto
            {
                Allowed = true,
                Reason = $"Next automatic retry at {record.NextAutomaticRetryAtUtc:yyyy-MM-dd HH:mm} UTC (automatic retry {record.AutomaticRetryCount + 1} of {recovery.MaxAttempts})."
            },
            OpenserveSubmissionFailureClass.Retryable => No($"Automatic retries are used up ({record.AutomaticRetryCount} of {recovery.MaxAttempts}). Retry manually."),
            OpenserveSubmissionFailureClass.Blocked => No("Never sent: fix the blocker, then retry manually. Blocked submissions are not retried automatically."),
            OpenserveSubmissionFailureClass.OutcomeUnknown => No(OpenserveSubmissionFailureClassifier.ExplainClass(OpenserveSubmissionFailureClass.OutcomeUnknown)),
            _ => No(OpenserveSubmissionFailureClassifier.ExplainClass(OpenserveSubmissionFailureClass.NonRetryable))
        };
    }

    private async Task<OpenserveAutomationPauseStateDto> AutomationStateAsync(Order order, bool forwarded, CancellationToken cancellationToken)
    {
        string? pausedBy = null;
        if (order.OpenserveAutomationPaused && order.OpenserveAutomationPausedByUserId is { } userId)
        {
            pausedBy = await _dbContext.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => (u.FirstName + " " + u.LastName).Trim() == string.Empty ? u.Email : (u.FirstName + " " + u.LastName).Trim())
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new OpenserveAutomationPauseStateDto
        {
            Paused = order.OpenserveAutomationPaused,
            PausedAtUtc = order.OpenserveAutomationPausedAtUtc,
            PausedBy = pausedBy,
            Reason = order.OpenserveAutomationPauseReason,
            CanPause = !order.OpenserveAutomationPaused && !forwarded,
            CanResume = order.OpenserveAutomationPaused,
            CannotPauseReason = order.OpenserveAutomationPaused ? null : forwarded ? "Already with Openserve — there is no submission left to pause." : null
        };
    }

    /// <summary>
    /// Merged from the existing records — integration logs (attempts, blocks,
    /// interruptions, failed status checks, cancellations), status history
    /// (Openserve status changes) and the audit log (pause/resume). No new table.
    /// </summary>
    private async Task<IReadOnlyList<OpenserveFulfilmentActivityDto>> ActivityAsync(Order order, OpenserveOrder? record, CancellationToken cancellationToken)
    {
        var items = new List<OpenserveFulfilmentActivityDto>();

        if (record is not null)
        {
            var logs = await _dbContext.OpenserveIntegrationLogs.AsNoTracking()
                .Where(l => l.OpenserveOrderId == record.Id && l.Direction == OpenserveIntegrationDirection.Outbound
                            && (l.OperationType == OpenserveOperationType.CreateOrder || l.OperationType == OpenserveOperationType.CancelOrder
                                || (l.OperationType == OpenserveOperationType.GetOrder && !l.IsSuccess)))
                .OrderByDescending(l => l.OccurredAtUtc)
                .Take(MaxActivityItems)
                .Select(l => new { l.OccurredAtUtc, l.OperationType, l.IsSuccess, l.ErrorSummary, l.ResponseStatusCode, l.SubmissionTrigger })
                .ToListAsync(cancellationToken);

            foreach (var l in logs)
            {
                var who = Capitalize(OpenserveOrderSubmissionService.TriggerLabel(l.SubmissionTrigger));
                var http = l.ResponseStatusCode is { } status ? $"HTTP {status} — " : string.Empty;
                items.Add(l.OperationType switch
                {
                    OpenserveOperationType.CreateOrder when l.IsSuccess => Item(l.OccurredAtUtc, "Submission", $"{who} — accepted by Openserve", null, "success"),
                    OpenserveOperationType.CreateOrder when l.ErrorSummary != null && l.ErrorSummary.StartsWith("BLOCKED", StringComparison.Ordinal) =>
                        Item(l.OccurredAtUtc, "Submission", $"{who} blocked — not sent", StripPrefix(l.ErrorSummary), "warning"),
                    OpenserveOperationType.CreateOrder when l.ErrorSummary != null && l.ErrorSummary.StartsWith("INTERRUPTED", StringComparison.Ordinal) =>
                        Item(l.OccurredAtUtc, "Submission", $"{who} interrupted — outcome unknown", StripPrefix(l.ErrorSummary), "failure"),
                    OpenserveOperationType.CreateOrder => Item(l.OccurredAtUtc, "Submission", $"{who} failed", http + l.ErrorSummary, "failure"),
                    OpenserveOperationType.CancelOrder => Item(l.OccurredAtUtc, "Cancellation", l.IsSuccess ? "Cancellation sent to Openserve" : "Cancellation request failed", l.IsSuccess ? null : http + l.ErrorSummary,
                        l.IsSuccess ? "info" : "failure"),
                    _ => Item(l.OccurredAtUtc, "Sync", "Status check with Openserve failed", http + l.ErrorSummary, "warning")
                });
            }

            var history = await _dbContext.OpenserveOrderStatusHistories.AsNoTracking()
                .Where(h => h.OpenserveOrderId == record.Id)
                .OrderByDescending(h => h.ReceivedAtUtc)
                .Take(MaxActivityItems)
                .Select(h => new { h.ReceivedAtUtc, h.NewRawState, h.NormalizedStatus, h.Description })
                .ToListAsync(cancellationToken);
            items.AddRange(history.Select(h => Item(h.ReceivedAtUtc, "Status", $"Openserve status: {h.NewRawState ?? h.NormalizedStatus.ToString()}", h.Description,
                h.NormalizedStatus == OpenserveProvisioningStatus.Completed ? "success" : "info")));
        }

        var automation = await _dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.Order && a.EntityId == order.Id
                        && (a.ActionType == AuditActionType.OpenserveAutomationPaused || a.ActionType == AuditActionType.OpenserveAutomationResumed))
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(MaxActivityItems)
            .Select(a => new
            {
                a.CreatedAtUtc,
                a.ActionType,
                a.MetadataJson,
                Actor = a.ActorUser != null ? ((a.ActorUser.FirstName + " " + a.ActorUser.LastName).Trim() == string.Empty ? a.ActorUser.Email : (a.ActorUser.FirstName + " " + a.ActorUser.LastName).Trim()) : null
            })
            .ToListAsync(cancellationToken);
        foreach (var a in automation)
        {
            var paused = a.ActionType == AuditActionType.OpenserveAutomationPaused;
            var title = $"Openserve automation {(paused ? "paused" : "resumed")}{(a.Actor is null ? string.Empty : $" by {a.Actor}")}";
            items.Add(Item(a.CreatedAtUtc, "Automation", title, ReasonFromMetadata(a.MetadataJson), paused ? "warning" : "info"));
        }

        return items.OrderByDescending(i => i.OccurredAtUtc).Take(MaxActivityItems).ToList();
    }

    // ─── helpers ────────────────────────────────────────────────────

    private Task<OpenserveOrder?> FindSalesOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        _dbContext.OpenserveOrders.AsNoTracking()
            .Where(o => o.OrderId == orderId && o.OrderType == OpenserveSubmissionRules.SalesOrderType)
            .OrderBy(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task AuditAsync(AuditActionType actionType, Order order, string summary, string? reason)
    {
        try
        {
            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = _currentUser.UserId.HasValue ? AuditActorType.Admin : AuditActorType.System,
                ActionType = actionType,
                EntityType = AuditEntityType.Order,
                EntityId = order.Id,
                EntityName = order.OrderNumber,
                Summary = summary,
                MetadataJson = JsonSerializer.Serialize(new { reason }),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Openserve automation audit write failed for order {OrderNumber}.", order.OrderNumber);
        }
    }

    private static OpenserveFulfilmentActivityDto Item(DateTime at, string kind, string title, string? detail, string tone) =>
        new() { OccurredAtUtc = at, Kind = kind, Title = title, Detail = string.IsNullOrWhiteSpace(detail) ? null : detail, Tone = tone };

    private static string? StripPrefix(string? summary)
    {
        if (summary is null) return null;
        var i = summary.IndexOf(": ", StringComparison.Ordinal);
        return i >= 0 ? summary[(i + 2)..] : summary;
    }

    private static string? ReasonFromMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Capitalize(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string? Clean(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= MaxPauseReasonLength ? trimmed : trimmed[..MaxPauseReasonLength];
    }

    private static string FormatAddress(Order order) =>
        string.Join(", ", new[] { order.AddressLine1, order.AddressLine2, order.Suburb, order.City, order.Province, order.PostalCode }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

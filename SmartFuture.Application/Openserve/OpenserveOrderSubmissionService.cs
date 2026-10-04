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
/// Openserve Product Order submission — the ONE coordinator every path
/// goes through (<see cref="SubmitAsync"/>): the automatic trigger, Admin
/// Send/Retry, the recovery worker and the safety sweep. Deliberately does
/// NOT process callbacks/events/reconciliation results — that's
/// <see cref="IOpenserveOrderUpdatePipeline"/>.
///
/// Duplicate protection (Openserve has no idempotency key, and an order
/// can't be looked up by our External Reference Number):
///   1. Claim before sending. A new record is inserted as Submitting
///      (unique ExternalReferenceNumber index — a second insert for the
///      same order fails); an existing record moves Failed → Submitting
///      through one conditional UPDATE. Whoever loses either race sends
///      nothing.
///   2. Once Openserve accepted the order (SubmittedAtUtc / Openserve
///      order id / any Openserve-side status) Create Order is never sent
///      again — reconciliation owns it from there.
///   3. A request that may have reached Openserve (timeout, dropped
///      connection, 5xx, unreadable 2xx, interrupted claim) is classified
///      OutcomeUnknown and is never resent automatically; Admin must
///      confirm with Openserve first.
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

    public OpenserveOrderSubmissionService(IAppDbContext dbContext, IOpenserveApiClient client, IOpenserveSubscriberReferenceGenerator subscriberReferenceGenerator, IOpenserveRuntimeConfigProvider configProvider, IAuditService auditService,
        ICurrentUserService currentUser, ILogger<OpenserveOrderSubmissionService> logger)
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
        var result = await SubmitAsync(new OpenserveSubmissionRequest(orderId, OpenserveSubmissionTrigger.AutomaticInitial) { NetworkAccountId = networkAccountId }, cancellationToken);
        if (result.IsSuccess) return Result.Success(result.Message);

        // Fire-and-forget trigger: "nothing to do here" (disabled, not Fibre,
        // already handled, paused) is not an error for the caller.
        return result.Code is ErrorCodes.NOT_FOUND or ErrorCodes.EXCEPTION
            ? Result.Failure(result.Code, result.Message)
            : Result.Success(result.Message);
    }

    public async Task<Result<OpenserveOrderDto>> AdminRetrySubmissionAsync(Guid openserveOrderId, bool confirmOutcomeUnknown = false, CancellationToken cancellationToken = default)
    {
        var orderId = await _dbContext.OpenserveOrders.AsNoTracking().Where(o => o.Id == openserveOrderId).Select(o => (Guid?)o.OrderId).FirstOrDefaultAsync(cancellationToken);
        if (orderId is null) return Result<OpenserveOrderDto>.Failure(ErrorCodes.NOT_FOUND, "Openserve order not found.");

        var attempt = await SubmitAsync(new OpenserveSubmissionRequest(orderId.Value, OpenserveSubmissionTrigger.AdminManual) { ConfirmOutcomeUnknown = confirmOutcomeUnknown }, cancellationToken);
        if (!attempt.IsSuccess) return Result<OpenserveOrderDto>.Failure(attempt.Code ?? ErrorCodes.VALIDATION_ERROR, attempt.Message);
        if (attempt.Data!.Outcome == OpenserveSubmissionOutcome.Blocked) return Result<OpenserveOrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, attempt.Message);

        var dto = await MapToDtoAsync(openserveOrderId, cancellationToken);
        return dto is null
            ? Result<OpenserveOrderDto>.Failure(ErrorCodes.EXCEPTION, "Retry completed but the record could not be reloaded.")
            : Result<OpenserveOrderDto>.Success(dto, attempt.Message);
    }

    public async Task<Result<OpenserveSubmissionAttemptDto>> SubmitAsync(OpenserveSubmissionRequest request, CancellationToken cancellationToken = default)
    {
        var trigger = request.Trigger;
        var settings = _configProvider.Current;
        if (!settings.Enabled)
        {
            // The global kill switch covers every path, manual ones included.
            _logger.LogDebug("[Openserve] Submission ({Trigger}) skipped for order {OrderId} — integration disabled.", trigger, request.OrderId);
            return Refused(ErrorCodes.VALIDATION_ERROR, "Openserve integration is disabled.");
        }

        try
        {
            var order = await _dbContext.Orders.AsNoTracking().Include(o => o.ServicePackage).FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);
            if (order is null) return Refused(ErrorCodes.NOT_FOUND, "Order not found.");

            // Defensive — callers already filter to Fibre, but this service
            // must never touch Security/Voice/LTE/Wireless.
            if (order.PackageType != Shared.Enums.ServicePackages.ServicePackageType.Fibre)
                return Refused(ErrorCodes.VALIDATION_ERROR, "Not a Fibre order — Openserve submission does not apply.");

            var gate = OpenserveSubmissionRules.OrderGateReason(order);
            if (gate is not null)
            {
                _logger.LogInformation("[Openserve] Submission ({Trigger}) not allowed for order {OrderNumber}: {Reason}", trigger, order.OrderNumber, gate);
                return Refused(ErrorCodes.VALIDATION_ERROR, gate);
            }

            var networkAccount = await ResolveNetworkAccountAsync(order.Id, request.NetworkAccountId, cancellationToken);
            if (networkAccount is null)
            {
                return request.NetworkAccountId.HasValue
                    ? Refused(ErrorCodes.NOT_FOUND, "Network account not found.")
                    : Refused(ErrorCodes.VALIDATION_ERROR, "No network account has been reserved for this order yet (payment not applied), so it can't be sent to Openserve.");
            }

            var now = DateTime.UtcNow;
            var existing = await FindSalesOrderAsync(order.Id, cancellationToken);

            OpenserveOrder openserveOrder;
            var createdNow = false;
            if (existing is not null)
            {
                var (refusal, validated) = await CheckExistingRecordAsync(existing, request, now, settings, cancellationToken);
                if (refusal is not null) return refusal;

                var claimed = await OpenserveSubmissionClaim.TryClaimAsync(_dbContext, validated, trigger, now, cancellationToken);
                if (!claimed) return Refused(ErrorCodes.CONFLICT, "Another submission attempt for this order is in progress or has just completed.");

                openserveOrder = await LoadTrackedFreshAsync(existing.Id, cancellationToken);
            }
            else
            {
                if (trigger == OpenserveSubmissionTrigger.BackgroundRetry)
                    return Refused(ErrorCodes.CONFLICT, "Nothing to retry — no Openserve submission record exists for this order.");

                openserveOrder = new OpenserveOrder
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    ExternalReferenceNumber = $"SF-{order.OrderNumber}",
                    SubscriberReferenceNumber = networkAccount.OpenserveSubscriberReferenceNumber,
                    OrderType = OpenserveSubmissionRules.SalesOrderType,
                    NormalizedStatus = OpenserveProvisioningStatus.Submitting,
                    LastSubmissionAttemptAtUtc = now,
                    LastSubmissionTrigger = trigger,
                    RetryCount = 0,
                    CreatedAtUtc = now
                };
                _dbContext.OpenserveOrders.Add(openserveOrder);
                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex)
                {
                    // Unique ExternalReferenceNumber: someone else created the
                    // record for this order between our check and our insert.
                    _dbContext.OpenserveOrders.Entry(openserveOrder).State = EntityState.Detached;
                    _logger.LogInformation(ex, "[Openserve] Submission ({Trigger}) for order {OrderNumber} lost the create race — another attempt owns it.", trigger, order.OrderNumber);
                    return Refused(ErrorCodes.CONFLICT, "Another submission attempt for this order is already in progress.");
                }
                createdNow = true;
            }

            return await RunClaimedAttemptAsync(openserveOrder, order, networkAccount, trigger, createdNow, settings, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error submitting order {OrderId} to Openserve ({Trigger}).", request.OrderId, trigger);
            return Refused(ErrorCodes.EXCEPTION, "An unexpected error occurred while submitting to Openserve.");
        }
    }

    public async Task<int> ResolveStaleSubmissionsAsync(CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        var staleMinutes = Math.Max(1, settings.SubmissionRecovery.StaleSubmissionMinutes);
        var cutoff = DateTime.UtcNow.AddMinutes(-staleMinutes);

        var staleIds = await _dbContext.OpenserveOrders.AsNoTracking()
            .Where(o => o.NormalizedStatus == OpenserveProvisioningStatus.Submitting && (o.LastSubmissionAttemptAtUtc ?? o.UpdatedAtUtc ?? o.CreatedAtUtc) <= cutoff)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        var resolved = 0;
        foreach (var id in staleIds)
        {
            if (await MarkInterruptedAsync(id, DateTime.UtcNow, staleMinutes, cancellationToken)) resolved++;
        }
        return resolved;
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

    // ─── coordinator internals ──────────────────────────────────────

    /// <summary>
    /// Rules for an order that already has a submission record. Returns a refusal,
    /// or the exact snapshot that passed — the claim is conditional on that
    /// snapshot, so nothing that changed in between can slip through.
    /// </summary>
    private async Task<(Result<OpenserveSubmissionAttemptDto>? Refusal, OpenserveOrder Validated)> CheckExistingRecordAsync(OpenserveOrder existing, OpenserveSubmissionRequest request, DateTime now,
        OpenserveFulfilmentSettings settings, CancellationToken cancellationToken)
    {
        if (OpenserveSubmissionRules.IsForwarded(existing))
        {
            var reference = existing.OpenserveOrderName ?? existing.OpenserveOrderId ?? existing.ExternalReferenceNumber;
            return (Refused(ErrorCodes.CONFLICT, $"Already submitted to Openserve (order {reference}, status {existing.NormalizedStatus}). Status updates come from reconciliation — Create Order is never sent twice."), existing);
        }

        // The automatic trigger and the sweep only ever create the first
        // record; what happens to an existing one is the worker's or Admin's call.
        if (request.Trigger is OpenserveSubmissionTrigger.AutomaticInitial or OpenserveSubmissionTrigger.SafetySweep)
            return (Refused(ErrorCodes.CONFLICT, $"An Openserve submission record already exists for this order (status={existing.NormalizedStatus})."), existing);

        if (existing.NormalizedStatus == OpenserveProvisioningStatus.Submitting)
        {
            var staleMinutes = settings.SubmissionRecovery.StaleSubmissionMinutes;
            if (!OpenserveSubmissionRules.IsStaleSubmitting(existing, now, staleMinutes))
                return (Refused(ErrorCodes.CONFLICT, "A submission attempt for this order is already in progress."), existing);

            // Interrupted attempt: record it as outcome-unknown, re-read, and
            // apply that class's rules below.
            await MarkInterruptedAsync(existing.Id, now, staleMinutes, cancellationToken);
            existing = (await FindSalesOrderAsync(existing.OrderId, cancellationToken))!;
            if (existing.NormalizedStatus == OpenserveProvisioningStatus.Submitting)
                return (Refused(ErrorCodes.CONFLICT, "A submission attempt for this order is already in progress."), existing);
        }

        if (existing.NormalizedStatus is not (OpenserveProvisioningStatus.Failed or OpenserveProvisioningStatus.NotSubmitted))
            return (Refused(ErrorCodes.CONFLICT, $"This Openserve submission can't be retried (status {existing.NormalizedStatus})."), existing);

        if (request.Trigger == OpenserveSubmissionTrigger.BackgroundRetry)
        {
            if (existing.LastFailureClass != OpenserveSubmissionFailureClass.Retryable)
                return (Refused(ErrorCodes.CONFLICT, $"Only Retryable failures are resent automatically (this one is {existing.LastFailureClass})."), existing);
            if (existing.NextAutomaticRetryAtUtc is null || existing.NextAutomaticRetryAtUtc > now)
                return (Refused(ErrorCodes.CONFLICT, "No automatic retry is due for this order."), existing);
        }

        if (request.Trigger == OpenserveSubmissionTrigger.AdminManual && existing.LastFailureClass == OpenserveSubmissionFailureClass.OutcomeUnknown && !request.ConfirmOutcomeUnknown)
        {
            return (Refused(ErrorCodes.CONFLICT,
                $"Openserve may already have received this order (the last attempt's outcome is unknown). Confirm with Openserve that no order exists for reference {existing.ExternalReferenceNumber}, then retry with that confirmation."), existing);
        }

        return (null, existing);
    }

    /// <summary>We hold the claim (record is Submitting). Re-validate everything, then send exactly one request.</summary>
    private async Task<Result<OpenserveSubmissionAttemptDto>> RunClaimedAttemptAsync(OpenserveOrder openserveOrder, Order order, NetworkAccount networkAccount, OpenserveSubmissionTrigger trigger, bool createdNow,
        OpenserveFulfilmentSettings settings, CancellationToken cancellationToken)
    {
        var requestStarted = false;
        try
        {
            // Reserve the subscriber reference once, on the durable
            // NetworkAccount row — persists across every retry.
            if (string.IsNullOrWhiteSpace(networkAccount.OpenserveSubscriberReferenceNumber))
            {
                networkAccount.OpenserveSubscriberReferenceNumber = _subscriberReferenceGenerator.Generate(networkAccount, order);
                networkAccount.UpdatedAtUtc = DateTime.UtcNow;
            }
            openserveOrder.SubscriberReferenceNumber ??= networkAccount.OpenserveSubscriberReferenceNumber;
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Always the package's CURRENT enabled mapping — a corrected mapping
            // is picked up, a disabled one is never used.
            var mapping = await _dbContext.PackageOpenserveMappings.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ServicePackageId == order.ServicePackageId && m.IsEnabled, cancellationToken);

            var blocker = OpenserveSubmissionRules.PreflightBlocker(order, mapping, settings);
            if (blocker is { } b)
            {
                openserveOrder.NormalizedStatus = OpenserveProvisioningStatus.Failed;
                openserveOrder.LastFailureClass = OpenserveSubmissionFailureClass.Blocked;
                openserveOrder.LastFailureCode = b.Code;
                openserveOrder.LastFailureMessage = b.Reason;
                openserveOrder.NextAutomaticRetryAtUtc = null;
                if (!createdNow) openserveOrder.RetryCount += 1;
                openserveOrder.UpdatedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                await LogBlockedSubmissionAsync(openserveOrder, b.Reason, trigger, cancellationToken);
                await EmitAuditAsync(AuditActionType.OpenserveOrderSubmissionFailed, openserveOrder, order, $"Openserve submission ({TriggerLabel(trigger)}) blocked for order {order.OrderNumber}: {b.Reason}");
                return Attempt(openserveOrder, OpenserveSubmissionOutcome.Blocked, requestSent: false, $"Openserve submission blocked: {b.Reason}");
            }

            openserveOrder.PackageOpenserveMappingId = mapping!.Id;
            requestStarted = true;
            return await ExecuteSubmissionCallAsync(openserveOrder, order, mapping, networkAccount, trigger, settings, cancellationToken);
        }
        catch (Exception ex) when (!requestStarted)
        {
            // Failed before anything was sent — safe to try again later.
            _logger.LogError(ex, "[Openserve] Submission ({Trigger}) for order {OrderNumber} failed before sending.", trigger, order.OrderNumber);
            await ReleaseClaimAfterInternalErrorAsync(openserveOrder.Id, settings, CancellationToken.None);
            return Refused(ErrorCodes.EXCEPTION, "An unexpected error occurred before the order was sent to Openserve. Nothing was sent.");
        }
        // An exception after the request started (e.g. host shutdown mid-call,
        // or saving Openserve's answer failed) leaves the claim in Submitting
        // on purpose: the stale-claim rule later records it as OutcomeUnknown,
        // the only safe reading when we can't tell whether the POST landed.
    }

    private async Task<Result<OpenserveSubmissionAttemptDto>> ExecuteSubmissionCallAsync(OpenserveOrder openserveOrder, Order order, PackageOpenserveMapping mapping, NetworkAccount networkAccount,
        OpenserveSubmissionTrigger trigger, OpenserveFulfilmentSettings settings, CancellationToken cancellationToken)
    {
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
            // MDU place values come only from Product Qualification
            // (OpenserveBuildingMatcher), never from customer free text —
            // Postman UC 1: "use as exactly per product qualification API".
            BuildingName = order.OpenserveBuildingName,
            Floor = order.OpenserveFloor,
            Unit = order.OpenserveUnit,
            BuildingNumId = order.OpenserveBuildingNumId,
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
        var now = DateTime.UtcNow;

        var failure = result.IsSuccess ? default : OpenserveSubmissionFailureClassifier.Classify(result.HttpStatusCode, result.ErrorCode);

        _dbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(),
            OpenserveOrderId = openserveOrder.Id,
            Direction = OpenserveIntegrationDirection.Outbound,
            OperationType = OpenserveOperationType.CreateOrder,
            MessageId = result.MessageId,
            HttpMethod = result.HttpMethod,
            Endpoint = result.Endpoint,
            RequestHeadersJson = result.RequestHeadersJson,
            RequestBodyJson = result.RequestBodyJson,
            ResponseStatusCode = result.HttpStatusCode,
            ResponseBodyJson = result.ResponseBodyJson,
            OccurredAtUtc = now,
            IsSuccess = result.IsSuccess,
            ErrorSummary = result.IsSuccess ? null : Truncate($"{result.ErrorCode}: {result.ErrorMessage}", 2000),
            SubmissionTrigger = trigger
        });

        openserveOrder.LastMessageId = result.MessageId;
        openserveOrder.RetryCount += 1;
        openserveOrder.AutomaticRetryCount = trigger == OpenserveSubmissionTrigger.BackgroundRetry ? openserveOrder.AutomaticRetryCount + 1 : 0;
        openserveOrder.UpdatedAtUtc = now;

        if (result.IsSuccess)
        {
            openserveOrder.NormalizedStatus = OpenserveProvisioningStatus.Submitted;
            openserveOrder.RawState = result.Outcome?.ParsedState ?? "Validated";
            openserveOrder.OpenserveOrderId ??= result.Outcome?.ParsedOrderId;
            openserveOrder.SubmittedAtUtc = now;
            openserveOrder.LastOpenserveUpdateAtUtc = now;
            openserveOrder.LastFailureCode = null;
            openserveOrder.LastFailureMessage = null;
            openserveOrder.LastFailureClass = OpenserveSubmissionFailureClass.None;
            openserveOrder.NextAutomaticRetryAtUtc = null;

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
                ReceivedAtUtc = now,
                EventOccurredAtUtc = now,
                ProcessingResult = "Applied",
                CreatedAtUtc = now
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(trigger == OpenserveSubmissionTrigger.AdminManual ? AuditActionType.OpenserveManualRetry : AuditActionType.OpenserveOrderSubmitted, openserveOrder, order,
                $"Order {order.OrderNumber} submitted to Openserve ({TriggerLabel(trigger)}, ExternalReferenceNumber={openserveOrder.ExternalReferenceNumber}).");
            return Attempt(openserveOrder, OpenserveSubmissionOutcome.Submitted, requestSent: true, "Openserve accepted the order.");
        }

        openserveOrder.NormalizedStatus = OpenserveProvisioningStatus.Failed;
        openserveOrder.LastFailureCode = Truncate(result.ErrorCode, 120);
        openserveOrder.LastFailureMessage = Truncate(result.ErrorMessage, 2000);
        openserveOrder.LastFailureClass = failure.Class;
        openserveOrder.NextAutomaticRetryAtUtc = NextAutomaticRetry(openserveOrder, settings.SubmissionRecovery, now);

        await _dbContext.SaveChangesAsync(cancellationToken);

        await EmitAuditAsync(AuditActionType.OpenserveOrderSubmissionFailed, openserveOrder, order,
            $"Openserve submission ({TriggerLabel(trigger)}) failed for order {order.OrderNumber}: {result.ErrorCode} {result.ErrorMessage} [{failure.Class}]");
        return Attempt(openserveOrder, OpenserveSubmissionOutcome.Failed, requestSent: true, $"Openserve submission failed: {result.ErrorMessage} {failure.Explanation}");
    }

    private static DateTime? NextAutomaticRetry(OpenserveOrder openserveOrder, OpenserveSubmissionRecoverySettings recovery, DateTime now)
    {
        if (openserveOrder.LastFailureClass != OpenserveSubmissionFailureClass.Retryable || !recovery.Enabled) return null;
        var nextNumber = openserveOrder.AutomaticRetryCount + 1;
        return nextNumber <= recovery.MaxAttempts ? now + OpenserveSubmissionRules.BackoffDelay(nextNumber, recovery) : null;
    }

    /// <summary>Stale Submitting → Failed / OutcomeUnknown, recorded once (conditional UPDATE).</summary>
    private async Task<bool> MarkInterruptedAsync(Guid openserveOrderId, DateTime now, int staleMinutes, CancellationToken cancellationToken)
    {
        var cutoff = now.AddMinutes(-Math.Max(1, staleMinutes));
        var (_, explanation) = OpenserveSubmissionFailureClassifier.Classify(null, OpenserveApiErrorCodes.Interrupted);
        var rows = await _dbContext.OpenserveOrders
            .Where(o => o.Id == openserveOrderId && o.NormalizedStatus == OpenserveProvisioningStatus.Submitting && (o.LastSubmissionAttemptAtUtc ?? o.UpdatedAtUtc ?? o.CreatedAtUtc) <= cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.NormalizedStatus, OpenserveProvisioningStatus.Failed)
                .SetProperty(o => o.LastFailureClass, OpenserveSubmissionFailureClass.OutcomeUnknown)
                .SetProperty(o => o.LastFailureCode, OpenserveApiErrorCodes.Interrupted)
                .SetProperty(o => o.LastFailureMessage, explanation)
                .SetProperty(o => o.NextAutomaticRetryAtUtc, (DateTime?)null)
                .SetProperty(o => o.UpdatedAtUtc, now), cancellationToken);
        if (rows != 1) return false;

        var trigger = await _dbContext.OpenserveOrders.AsNoTracking().Where(o => o.Id == openserveOrderId).Select(o => o.LastSubmissionTrigger).FirstOrDefaultAsync(cancellationToken);
        _dbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(),
            OpenserveOrderId = openserveOrderId,
            Direction = OpenserveIntegrationDirection.Outbound,
            OperationType = OpenserveOperationType.CreateOrder,
            OccurredAtUtc = now,
            IsSuccess = false,
            ErrorSummary = Truncate($"INTERRUPTED (outcome unknown): {explanation}", 2000),
            SubmissionTrigger = trigger
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogWarning("[Openserve] Submission claim for OpenserveOrder {Id} went stale — recorded as outcome unknown; it will not be resent automatically.", openserveOrderId);
        return true;
    }

    private async Task ReleaseClaimAfterInternalErrorAsync(Guid openserveOrderId, OpenserveFulfilmentSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            var recovery = settings.SubmissionRecovery;
            DateTime? next = recovery.Enabled && recovery.MaxAttempts > 0 ? now + OpenserveSubmissionRules.BackoffDelay(1, recovery) : null;
            await _dbContext.OpenserveOrders
                .Where(o => o.Id == openserveOrderId && o.NormalizedStatus == OpenserveProvisioningStatus.Submitting)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.NormalizedStatus, OpenserveProvisioningStatus.Failed)
                    .SetProperty(o => o.LastFailureClass, OpenserveSubmissionFailureClass.Retryable)
                    .SetProperty(o => o.LastFailureCode, "INTERNAL_ERROR")
                    .SetProperty(o => o.LastFailureMessage, "SmartFuture hit an internal error before sending the order. Nothing was sent to Openserve.")
                    .SetProperty(o => o.NextAutomaticRetryAtUtc, next)
                    .SetProperty(o => o.UpdatedAtUtc, now), cancellationToken);
        }
        catch (Exception ex)
        {
            // The claim then goes stale and is recorded as outcome unknown — conservative, never a duplicate.
            _logger.LogError(ex, "[Openserve] Could not release the submission claim for OpenserveOrder {Id}.", openserveOrderId);
        }
    }

    private async Task<NetworkAccount?> ResolveNetworkAccountAsync(Guid orderId, Guid? networkAccountId, CancellationToken cancellationToken)
    {
        if (networkAccountId.HasValue)
            return await _dbContext.NetworkAccounts.FirstOrDefaultAsync(n => n.Id == networkAccountId.Value && n.OrderId == orderId, cancellationToken);

        var accounts = await _dbContext.NetworkAccounts.Where(n => n.OrderId == orderId).ToListAsync(cancellationToken);
        return OpenserveSubmissionRules.PickNetworkAccount(accounts);
    }

    private Task<OpenserveOrder?> FindSalesOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        _dbContext.OpenserveOrders.AsNoTracking()
            .Where(o => o.OrderId == orderId && o.OrderType == OpenserveSubmissionRules.SalesOrderType)
            .OrderBy(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The claim was written with ExecuteUpdate (bypassing the change tracker) — make sure we work on the row as it is now.</summary>
    private async Task<OpenserveOrder> LoadTrackedFreshAsync(Guid openserveOrderId, CancellationToken cancellationToken)
    {
        var tracked = _dbContext.OpenserveOrders.Local.FirstOrDefault(o => o.Id == openserveOrderId);
        if (tracked is not null)
        {
            await _dbContext.OpenserveOrders.Entry(tracked).ReloadAsync(cancellationToken);
            return tracked;
        }
        return await _dbContext.OpenserveOrders.FirstAsync(o => o.Id == openserveOrderId, cancellationToken);
    }

    /// <summary>
    /// A blocked submission sends NOTHING to Openserve, but it must still be
    /// visible in Admin → Integrations → Openserve → Logs next to real calls.
    /// HttpMethod/Endpoint stay null — that's how health/connectivity
    /// queries tell "never sent" apart from "sent and failed".
    /// </summary>
    private async Task LogBlockedSubmissionAsync(OpenserveOrder openserveOrder, string reason, OpenserveSubmissionTrigger trigger, CancellationToken cancellationToken)
    {
        _dbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(),
            OpenserveOrderId = openserveOrder.Id,
            Direction = OpenserveIntegrationDirection.Outbound,
            OperationType = OpenserveOperationType.CreateOrder,
            OccurredAtUtc = DateTime.UtcNow,
            IsSuccess = false,
            ErrorSummary = Truncate($"BLOCKED (not sent to Openserve): {reason}", 2000),
            SubmissionTrigger = trigger
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
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
        LastFailureClass = o.LastFailureClass.ToString(),
        LastSubmissionAttemptAtUtc = o.LastSubmissionAttemptAtUtc,
        LastSubmissionTrigger = o.LastSubmissionTrigger?.ToString(),
        AutomaticRetryCount = o.AutomaticRetryCount,
        NextAutomaticRetryAtUtc = o.NextAutomaticRetryAtUtc,
        CreatedAtUtc = o.CreatedAtUtc,
        UpdatedAtUtc = o.UpdatedAtUtc
    });

    public static string TriggerLabel(OpenserveSubmissionTrigger? trigger) => trigger switch
    {
        OpenserveSubmissionTrigger.AutomaticInitial => "automatic submission",
        OpenserveSubmissionTrigger.AdminManual => "Admin submission",
        OpenserveSubmissionTrigger.BackgroundRetry => "automatic retry",
        OpenserveSubmissionTrigger.SafetySweep => "safety sweep",
        _ => "submission"
    };

    private static Result<OpenserveSubmissionAttemptDto> Refused(string code, string message) => Result<OpenserveSubmissionAttemptDto>.Failure(code, message);

    private static Result<OpenserveSubmissionAttemptDto> Attempt(OpenserveOrder openserveOrder, string outcome, bool requestSent, string message) =>
        Result<OpenserveSubmissionAttemptDto>.Success(new OpenserveSubmissionAttemptDto
        {
            OrderId = openserveOrder.OrderId,
            OpenserveOrderRecordId = openserveOrder.Id,
            Outcome = outcome,
            RequestSent = requestSent,
            FailureClass = openserveOrder.LastFailureClass.ToString(),
            Message = message
        }, message);

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

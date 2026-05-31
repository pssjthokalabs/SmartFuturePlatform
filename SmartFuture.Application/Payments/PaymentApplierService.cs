using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.ServiceChanges;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments;

/// <summary>
/// Canonical service for applying Payment state transitions across Payment/Invoice/Order.
/// Used by both admin (PaymentService.AdminUpdateStatusAsync) and webhook
/// (WebhookInboxService) callers so the arithmetic is computed in exactly one place.
/// </summary>
public class PaymentApplierService : IPaymentApplierService
{
    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly INotificationService _notificationService;
    private readonly INetworkAccountService _networkAccountService;
    private readonly IServiceChangeRequestService _serviceChangeRequests;
    private readonly ICurrentUserService _currentUser;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PaymentApplierService> _logger;

    public PaymentApplierService(IAppDbContext dbContext, IAuditService auditService, INotificationService notificationService, INetworkAccountService networkAccountService,
        IServiceChangeRequestService serviceChangeRequests, ICurrentUserService currentUser, IHostEnvironment env, ILogger<PaymentApplierService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _notificationService = notificationService;
        _networkAccountService = networkAccountService;
        _serviceChangeRequests = serviceChangeRequests;
        _currentUser = currentUser;
        _env = env;
        _logger = logger;
    }

    public async Task<Result<PaymentDto>> ApplyStatusChangeAsync(ApplyPaymentStatusChangeRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return Result<PaymentDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

        if (request.PaymentId == Guid.Empty)
            return Result<PaymentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "PaymentId is required.");

        // SQL Server provider is registered with the retrying execution
        // strategy. That forbids raw user-initiated transactions:
        //   "The configured execution strategy 'SqlServerRetryingExecutionStrategy'
        //    does not support user-initiated transactions."
        // The entire transactional unit must run inside ExecuteAsync so
        // EF can retry it transparently on transient failures.
        //
        // Idempotency note: the delegate body short-circuits when the
        // payment status is already at the target value, so a retry
        // after a successful commit is a no-op. Post-commit side
        // effects (notifications, provisioning, service-change hooks)
        // live OUTSIDE the strategy delegate so they fire exactly once
        // — never duplicated on retry.
        var strategy = _dbContext.CreateExecutionStrategy();

        // Out-of-band signals populated by the delegate so the
        // post-commit hooks know what to do.
        Payment? committedPayment = null;
        var committedPaymentNotFound = false;
        var committedWasNoOp = false;
        var committedInvoiceBecamePaid = false;
        Guid? committedHookOrderId = null;
        Guid? committedHookInvoiceId = null;
        Guid? committedHookPaymentId = null;

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                // On retry, EVERY local that survives between attempts
                // must be reset to its initial value — otherwise the
                // second attempt sees stale data from the first.
                committedPayment = null;
                committedPaymentNotFound = false;
                committedWasNoOp = false;
                committedInvoiceBecamePaid = false;
                committedHookOrderId = null;
                committedHookInvoiceId = null;
                committedHookPaymentId = null;

                await using var transaction = await _dbContext.BeginTransactionAsync(cancellationToken);

                try
                {
                    var payment = await _dbContext.Payments
                        .Include(p => p.Invoice).ThenInclude(i => i!.Order)
                        .Include(p => p.Invoice).ThenInclude(i => i!.LineItems)
                        .Include(p => p.LastStatusChangedByUser)
                        .FirstOrDefaultAsync(p => p.Id == request.PaymentId, cancellationToken);

                    if (payment is null)
                    {
                        // No mutation occurred — safe to commit nothing
                        // and let the outer code translate to NotFound.
                        committedPaymentNotFound = true;
                        await transaction.CommitAsync(cancellationToken);
                        return;
                    }

                    var previous = payment.Status;
                    var newStatus = request.NewStatus;
                    var now = DateTime.UtcNow;

                    // Pre-mutation updates: gateway references are always allowed to refresh
                    // (idempotent: same value gets re-written, no harm).
                    if (!string.IsNullOrWhiteSpace(request.GatewayTransactionId))
                        payment.GatewayTransactionId = request.GatewayTransactionId.Trim();
                    if (!string.IsNullOrWhiteSpace(request.GatewayReference))
                        payment.GatewayReference = request.GatewayReference.Trim();

                    // Idempotency: if the status is unchanged, do NOT re-apply arithmetic.
                    if (previous == newStatus)
                    {
                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        committedPayment = payment;
                        committedWasNoOp = true;
                        return;
                    }

                    payment.Status = newStatus;
                    payment.LastStatusChangedByUserId = _currentUser.UserId;

                    switch (newStatus)
                    {
                        case PaymentStatus.Completed:
                            payment.PaidAtUtc = request.PaidAtUtc ?? payment.PaidAtUtc ?? now;
                            break;
                        case PaymentStatus.Failed:
                            payment.FailedAtUtc ??= now;
                            if (!string.IsNullOrWhiteSpace(request.FailureReason))
                                payment.FailureReason = request.FailureReason.Trim();
                            break;
                        case PaymentStatus.Refunded:
                        case PaymentStatus.Reversed:
                            payment.RefundedAtUtc ??= now;
                            break;
                    }

                    var wasCompleted = previous == PaymentStatus.Completed;
                    var isCompleted = newStatus == PaymentStatus.Completed;

                    OrderStatus? orderPrevStatus = null;
                    OrderStatus? orderNewStatus = null;
                    var invoiceBecamePaid = false;

                    if (payment.Invoice is not null && wasCompleted != isCompleted)
                    {
                        // ─── UAT live test-amount override settlement ────────
                        //
                        // When a Payment carries IsTestAmountOverrideApplied=true
                        // AND env is non-Production, treat the InvoiceAmountAtTime
                        // value (= the real invoice balance at the moment of
                        // charge) as the delta — not Payment.Amount (= the R10
                        // override). This is what lets a R10 Paystack charge
                        // satisfy a R100 / R370 invoice in UAT.
                        //
                        // Production NEVER honours the override flag, even if a
                        // stale row from a UAT restore is present. The R10 would
                        // post as a R10 PartiallyPaid contribution — exactly the
                        // "don't underpay production invoices" guarantee.
                        var settlementAmount = payment.Amount;
                        if (payment.IsTestAmountOverrideApplied)
                        {
                            if (_env.IsProduction())
                            {
                                _logger.LogError(
                                    "[PaymentOverrideApply] BLOCKED in Production — payment {PaymentNumber} carries IsTestAmountOverrideApplied=true. " +
                                    "Settling at provider amount {ProviderAmount} only; invoice {InvoiceNumber} will NOT be fully paid.",
                                    payment.PaymentNumber, payment.Amount, payment.Invoice.InvoiceNumber);
                            }
                            else if (payment.InvoiceAmountAtTime is > 0m)
                            {
                                settlementAmount = payment.InvoiceAmountAtTime.Value;
                                _logger.LogWarning(
                                    "[PaymentOverrideApply] UAT test override applied — settling invoice {InvoiceNumber} at full amount {InvoiceAmount} " +
                                    "despite provider charge of only {ProviderAmount}. payment={PaymentNumber} env={Environment}",
                                    payment.Invoice.InvoiceNumber, settlementAmount, payment.Amount,
                                    payment.PaymentNumber, _env.EnvironmentName);
                            }
                            else if (payment.Invoice.TotalAmount > 0m)
                            {
                                // Fallback when InvoiceAmountAtTime wasn't
                                // captured at initiate time (e.g. an earlier UAT
                                // initiation row predating that column). Use the
                                // current invoice total. NON-PRODUCTION ONLY —
                                // production path above already blocked.
                                settlementAmount = payment.Invoice.TotalAmount;
                                _logger.LogWarning(
                                    "[PaymentOverrideApply] InvoiceAmountAtTime missing for {PaymentNumber}; falling back to Invoice.TotalAmount={InvoiceTotal} in UAT.",
                                    payment.PaymentNumber, settlementAmount);
                            }
                        }

                        var delta = isCompleted ? settlementAmount : -settlementAmount;
                        var previousInvoiceStatus = payment.Invoice.Status;

                        ApplyPaymentToInvoice(payment.Invoice, delta, now);

                        invoiceBecamePaid =
                            payment.Invoice.Status == InvoiceStatus.Paid
                            && previousInvoiceStatus != InvoiceStatus.Paid;

                        if (payment.Invoice.Status == InvoiceStatus.Paid
                            && payment.Invoice.Order is not null
                            && payment.Invoice.Order.Status == OrderStatus.AwaitingPayment)
                        {
                            orderPrevStatus = payment.Invoice.Order.Status;
                            payment.Invoice.Order.Status = OrderStatus.PaymentReceived;
                            payment.Invoice.Order.LastStatusChangedByUserId = _currentUser.UserId;
                            orderNewStatus = payment.Invoice.Order.Status;
                        }

                        // ─── Go-live alignment ──────────────────────────────
                        //
                        // A monthly-service invoice becoming Paid promotes the
                        // order from PendingPayment → PendingActivation. From
                        // there only the admin "Activate Service" action can
                        // flip it to Active (because Openserve activation is
                        // manual).
                        //
                        // For an already-Active order (recurring monthly
                        // invoice paid by auto-debit or manually), this is
                        // where we advance Order.NextPayDateUtc — always from
                        // the invoice DueAtUtc, never from PaidAtUtc, so early
                        // payments don't drift the billing cadence forward.
                        if (invoiceBecamePaid
                            && payment.Invoice.Order is not null
                            && payment.Invoice.LineItems.Any(li => li.LineType == InvoiceLineItemType.ServicePackage))
                        {
                            var order = payment.Invoice.Order;
                            if (order.Status == OrderStatus.PendingPayment)
                            {
                                orderPrevStatus = order.Status;
                                order.Status = OrderStatus.PendingActivation;
                                order.LastStatusChangedByUserId = _currentUser.UserId;
                                orderNewStatus = order.Status;
                                _logger.LogInformation(
                                    "[OrderLifecycle] {OrderNumber} PendingPayment → PendingActivation (invoice {InvoiceNumber} paid)",
                                    order.OrderNumber, payment.Invoice.InvoiceNumber);
                            }

                            // Advance the billing anchor for already-Active
                            // orders. Anchor advances from the invoice's
                            // DueAtUtc (NOT PaidAtUtc) so an early payment
                            // doesn't shift the schedule earlier — that's the
                            // explicit go-live rule.
                            if (order.Status == OrderStatus.Active && payment.Invoice.DueAtUtc.HasValue)
                            {
                                var previousNextPay = order.NextPayDateUtc;
                                var basis = order.NextPayDateUtc ?? payment.Invoice.DueAtUtc.Value;
                                order.NextPayDateUtc = basis.AddDays(30);
                                _logger.LogInformation(
                                    "[OrderLifecycle] {OrderNumber} NextPayDateUtc advanced {Previous:o} → {Next:o} (basis={Basis:o}, paidAt={PaidAt:o})",
                                    order.OrderNumber, previousNextPay, order.NextPayDateUtc, basis, payment.PaidAtUtc);
                            }
                        }
                    }

                    await _dbContext.SaveChangesAsync(cancellationToken);

                    // Audit side effects. Payment integrity wins — audit failures
                    // log a warning and continue. (Previously these were
                    // un-protected inside the transaction, so an audit save
                    // failure rolled the entire payment back and the customer
                    // saw "An unexpected error occurred" despite Paystack having
                    // actually settled the money.)
                    try
                    {
                        await EmitPaymentAuditAsync(payment, previous, newStatus);
                    }
                    catch (Exception auditEx)
                    {
                        _logger.LogWarning(auditEx,
                            "[PaymentApplierAuditWarning] payment audit failed for {PaymentNumber} ({PaymentId}); continuing — payment integrity preserved.",
                            payment.PaymentNumber, payment.Id);
                    }

                    if (orderPrevStatus.HasValue && orderNewStatus.HasValue
                        && payment.Invoice?.Order is not null)
                    {
                        try
                        {
                            await EmitOrderStatusChangedAuditAsync(
                                payment.Invoice.Order, orderPrevStatus.Value, orderNewStatus.Value, payment.PaymentNumber);
                        }
                        catch (Exception auditEx)
                        {
                            _logger.LogWarning(auditEx,
                                "[PaymentApplierAuditWarning] order audit failed for {OrderNumber} after payment {PaymentNumber}; continuing.",
                                payment.Invoice.Order.OrderNumber, payment.PaymentNumber);
                        }
                    }

                    await transaction.CommitAsync(cancellationToken);

                    // Surface signals for the post-commit hooks (run
                    // OUTSIDE the strategy delegate so they fire exactly
                    // once even if the strategy retried).
                    committedPayment = payment;
                    committedInvoiceBecamePaid = invoiceBecamePaid;
                    if (invoiceBecamePaid && payment.Invoice?.OrderId is Guid invoiceOrderId)
                        committedHookOrderId = invoiceOrderId;
                    if (invoiceBecamePaid && payment.Invoice is not null)
                    {
                        committedHookInvoiceId = payment.Invoice.Id;
                        committedHookPaymentId = payment.Id;
                    }
                }
                catch
                {
                    try { await transaction.RollbackAsync(cancellationToken); } catch { /* swallow */ }
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            // Structured [PaymentApplierException] log — gives the
            // operator everything needed to diagnose without re-running.
            // We re-read the tracked Payment via the change tracker (cheap)
            // so the log carries the row's identity even on early
            // failures.
            var tracked = _dbContext.Payments.Local.FirstOrDefault(p => p.Id == request.PaymentId);
            _logger.LogError(ex,
                "[PaymentApplierException] paymentId={PaymentId} paymentNumber={PaymentNumber} invoiceId={InvoiceId} invoiceNumber={InvoiceNumber} " +
                "oldStatus={OldStatus} newStatus={NewStatus} gatewayReference={GatewayReference} " +
                "overrideApplied={Override} paymentAmount={PaymentAmount} invoiceAmountAtTime={InvoiceAmountAtTime} actualProviderAmount={ActualProviderAmount} " +
                "exceptionType={ExceptionType} exceptionMessage='{ExceptionMessage}'",
                request.PaymentId, tracked?.PaymentNumber, tracked?.InvoiceId, tracked?.Invoice?.InvoiceNumber,
                tracked?.Status, request.NewStatus, request.GatewayReference,
                tracked?.IsTestAmountOverrideApplied, tracked?.Amount, tracked?.InvoiceAmountAtTime, tracked?.ActualProviderAmount,
                ex.GetType().FullName, ex.Message);
            var safeMessage = $"{ex.GetType().Name}: {Truncate(ex.Message, 300)}";
            return Result<PaymentDto>.Failure(ErrorCodes.EXCEPTION, safeMessage);
        }

        // Translate delegate-only signals into the outer Result.
        if (committedPaymentNotFound)
            return Result<PaymentDto>.Failure(ErrorCodes.NOT_FOUND, "Payment not found.");
        if (committedPayment is null)
            return Result<PaymentDto>.Failure(ErrorCodes.EXCEPTION, "Apply completed without producing a payment reference.");

        if (committedWasNoOp)
            return Result<PaymentDto>.Success(MapToDto(committedPayment), "Payment status unchanged; gateway references refreshed.");

        // ─── Post-commit hooks ──────────────────────────────────────
        // Live OUTSIDE the strategy delegate so each one fires exactly
        // once per successful apply, never duplicated on retry. Each
        // hook is wrapped (TryNotifyInvoicePaidAsync /
        // TryProvisionNetworkAccountAsync already swallow + log) so a
        // failure here never affects the committed payment state.
        if (committedInvoiceBecamePaid && request.TriggerNotifications && committedPayment.Invoice is not null)
        {
            await TryNotifyInvoicePaidAsync(committedPayment.Invoice, committedPayment, cancellationToken);
        }

        if (committedHookOrderId is Guid hookOrderId)
        {
            await TryProvisionNetworkAccountAsync(hookOrderId, committedPayment.PaymentNumber, cancellationToken);
        }

        if (committedHookInvoiceId is Guid hookInvoiceId && committedHookPaymentId is Guid hookPaymentId)
        {
            try
            {
                await _serviceChangeRequests.OnInvoicePaidAsync(hookInvoiceId, hookPaymentId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Service-change auto-complete hook threw for invoice {InvoiceNumber}.", committedPayment.Invoice?.InvoiceNumber);
            }
        }

        return Result<PaymentDto>.Success(MapToDto(committedPayment), "Payment status applied.");
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    /// <summary>
    /// Canonical arithmetic. Adjusts Invoice.AmountPaid by deltaCompletedAmount and
    /// recomputes BalanceDue + Status accordingly. Floor at zero; status transitions are
    /// inferred from the totals so callers never have to set Invoice.Status directly.
    /// </summary>
    public static void ApplyPaymentToInvoice(Invoice invoice, decimal deltaCompletedAmount, DateTime now)
    {
        invoice.AmountPaid += deltaCompletedAmount;
        if (invoice.AmountPaid < 0) invoice.AmountPaid = 0;

        invoice.BalanceDue = invoice.TotalAmount - invoice.AmountPaid;
        if (invoice.BalanceDue < 0) invoice.BalanceDue = 0;

        if (invoice.AmountPaid >= invoice.TotalAmount && invoice.TotalAmount > 0)
        {
            invoice.Status = InvoiceStatus.Paid;
            if (invoice.PaidAtUtc is null) invoice.PaidAtUtc = now;
        }
        else if (invoice.AmountPaid > 0)
        {
            invoice.Status = InvoiceStatus.PartiallyPaid;
            invoice.PaidAtUtc = null;
        }
        else
        {
            if (invoice.Status == InvoiceStatus.Paid || invoice.Status == InvoiceStatus.PartiallyPaid)
            {
                invoice.Status = InvoiceStatus.Issued;
                invoice.PaidAtUtc = null;
            }
        }
    }

    private async Task EmitPaymentAuditAsync(Payment payment, PaymentStatus previous, PaymentStatus newStatus)
    {
        var actorType = _currentUser.UserId.HasValue ? AuditActorType.Admin : AuditActorType.System;

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = AuditActionType.PaymentStatusChanged,
            EntityType = AuditEntityType.Payment,
            EntityId = payment.Id,
            EntityName = payment.PaymentNumber,
            Summary = $"Payment status changed: {previous} -> {newStatus} ({payment.PaymentNumber})",
            MetadataJson = BuildMetadata(new
            {
                invoiceId = payment.InvoiceId,
                invoiceNumber = payment.Invoice?.InvoiceNumber,
                previous,
                newStatus,
                amount = payment.Amount,
                currencyCode = payment.CurrencyCode
            }),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private async Task EmitOrderStatusChangedAuditAsync(Order order, OrderStatus previous, OrderStatus newStatus, string paymentNumber)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.System,
            ActionType = AuditActionType.OrderStatusChanged,
            EntityType = AuditEntityType.Order,
            EntityId = order.Id,
            EntityName = order.OrderNumber,
            Summary = $"Order status changed by payment: {previous} -> {newStatus} ({order.OrderNumber})",
            MetadataJson = BuildMetadata(new
            {
                previous,
                newStatus,
                triggeredBy = "PaymentApplier",
                paymentNumber
            }),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private async Task TryNotifyInvoicePaidAsync(Invoice invoice, Payment payment, CancellationToken cancellationToken)
    {
        try
        {
            var contact = await _dbContext.Orders
                .Where(o => o.Id == invoice.OrderId)
                .Select(o => new
                {
                    o.UserId,
                    Email = o.Email ?? (o.User != null ? o.User.Email : null),
                    Phone = o.PhoneNumber ?? (o.User != null ? o.User.PhoneNumber : null)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (contact is null || string.IsNullOrWhiteSpace(contact.Email))
                return;

            await _notificationService.SendAsync(new SendNotificationRequestDto
            {
                UserId = contact.UserId,
                Channel = NotificationChannel.Email,
                Type = NotificationType.InvoicePaid,
                RecipientEmail = contact.Email,
                RecipientPhone = contact.Phone,
                Subject = $"Payment received: invoice {invoice.InvoiceNumber}",
                Body = $"Thank you. We have received your payment of {payment.Amount:0.00} {payment.CurrencyCode} for invoice {invoice.InvoiceNumber}.\n\nYour account is now up to date.",
                RelatedEntityType = nameof(Invoice),
                RelatedEntityId = invoice.Id
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch InvoicePaid notification for invoice {InvoiceId}", invoice.Id);
        }
    }

    private async Task TryProvisionNetworkAccountAsync(Guid orderId, string paymentNumber, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _networkAccountService.ProvisionForOrderAsync(
                orderId, NetworkAccountSource.SystemAutomated, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogInformation(
                    "Network provisioning hook (Payment {PaymentNumber}) skipped: {Code} {Message}",
                    paymentNumber, result.Code, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Network provisioning hook (Payment {PaymentNumber}) threw",
                paymentNumber);
        }
    }

    private static string? BuildMetadata(object payload)
    {
        try { return JsonSerializer.Serialize(payload); }
        catch { return null; }
    }

    private static PaymentDto MapToDto(Payment p) => new()
    {
        Id = p.Id,
        PaymentNumber = p.PaymentNumber,
        InvoiceId = p.InvoiceId,
        InvoiceNumber = p.Invoice?.InvoiceNumber,
        OrderId = p.Invoice?.OrderId,
        OrderNumber = p.Invoice?.Order?.OrderNumber,
        Status = p.Status,
        Method = p.Method,
        Amount = p.Amount,
        CurrencyCode = p.CurrencyCode,
        PaidAtUtc = p.PaidAtUtc,
        FailedAtUtc = p.FailedAtUtc,
        RefundedAtUtc = p.RefundedAtUtc,
        GatewayName = p.GatewayName,
        GatewayReference = p.GatewayReference,
        GatewayTransactionId = p.GatewayTransactionId,
        ExternalReference = p.ExternalReference,
        FailureReason = p.FailureReason,
        Notes = p.Notes,
        AdminNotes = p.AdminNotes,
        LastStatusChangedByUserId = p.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = p.LastStatusChangedByUser?.Email,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc
    };
}

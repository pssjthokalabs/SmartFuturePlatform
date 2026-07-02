using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using Microsoft.Extensions.Options;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.ServiceChanges;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Communication;
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
    private readonly IOptions<ServiceActivationSettings> _activationSettings;
    private readonly IServiceBillingScheduleService _billingSchedule;
    private readonly ILogger<PaymentApplierService> _logger;

    public PaymentApplierService(IAppDbContext dbContext, IAuditService auditService, INotificationService notificationService, INetworkAccountService networkAccountService,
        IServiceChangeRequestService serviceChangeRequests, ICurrentUserService currentUser, IHostEnvironment env,
        IOptions<ServiceActivationSettings> activationSettings, IServiceBillingScheduleService billingSchedule, ILogger<PaymentApplierService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _notificationService = notificationService;
        _networkAccountService = networkAccountService;
        _serviceChangeRequests = serviceChangeRequests;
        _currentUser = currentUser;
        _env = env;
        _activationSettings = activationSettings;
        _billingSchedule = billingSchedule;
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
        // Go-live auto-activation (Bug fix) — set when the monthly
        // invoice that just became Paid promoted Order.Status all the
        // way to Active because ServiceActivation:RequireManualOpenserve
        // Activation=false. Triggers a post-commit ProvisionForOrderAsync
        // so the NetworkAccount flips Pending → Active in the same step.
        var committedOrderAutoActivated = false;

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
                committedOrderAutoActivated = false;

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

                    // ─── Pure decision core ───────────────────────────────
                    // Build the input snapshot BEFORE mutation, hand it to
                    // PaymentSettlementCore, then apply the outcome to
                    // tracked entities below. Same decisions the inline
                    // implementation used to make — the core is 100%
                    // like-for-like, covered by PaymentSettlementCoreTests.
                    var settlementInput = new PaymentSettlementInput
                    {
                        PreviousPaymentStatus = previous,
                        NewPaymentStatus = newStatus,
                        PaymentAmount = payment.Amount,
                        IsTestAmountOverrideApplied = payment.IsTestAmountOverrideApplied,
                        InvoiceAmountAtTime = payment.InvoiceAmountAtTime,
                        Invoice = payment.Invoice is null ? null : new InvoiceState(
                            CurrentStatus: payment.Invoice.Status,
                            TotalAmount: payment.Invoice.TotalAmount,
                            CurrentAmountPaid: payment.Invoice.AmountPaid,
                            HasServiceLineItem: payment.Invoice.LineItems.Any(li =>
                                li.LineType == InvoiceLineItemType.ServicePackage
                                || li.LineType == InvoiceLineItemType.ProRata),
                            DueAtUtc: payment.Invoice.DueAtUtc,
                            CurrentPaidAtUtc: payment.Invoice.PaidAtUtc),
                        Order = payment.Invoice?.Order is null ? null : new OrderState(
                            CurrentStatus: payment.Invoice.Order.Status,
                            ActivatedAtUtc: payment.Invoice.Order.ActivatedAtUtc,
                            BillingAnchorDateUtc: payment.Invoice.Order.BillingAnchorDateUtc,
                            NextPayDateUtc: payment.Invoice.Order.NextPayDateUtc),
                        IsProduction = _env.IsProduction(),
                        RequireManualOpenserveActivation = _activationSettings.Value.RequireManualOpenserveActivation,
                        NowUtc = now,
                    };
                    var outcome = PaymentSettlementCore.Calculate(settlementInput);

                    // ─── No-op path (duplicate status change) ─────────────
                    // Gateway ref updates above have already been applied;
                    // commit them and short-circuit. Post-commit hooks below
                    // gate on committedInvoiceBecamePaid so they don't fire.
                    if (!outcome.ShouldApply)
                    {
                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        committedPayment = payment;
                        committedWasNoOp = true;
                        return;
                    }

                    payment.Status = newStatus;
                    payment.LastStatusChangedByUserId = _currentUser.UserId;

                    if (outcome.ShouldSetPaidAtUtc)
                        payment.PaidAtUtc = request.PaidAtUtc ?? payment.PaidAtUtc ?? now;
                    if (outcome.ShouldSetFailedAtUtc)
                    {
                        payment.FailedAtUtc ??= now;
                        if (!string.IsNullOrWhiteSpace(request.FailureReason))
                            payment.FailureReason = request.FailureReason.Trim();
                    }
                    if (outcome.ShouldSetRefundedAtUtc)
                        payment.RefundedAtUtc ??= now;

                    OrderStatus? orderPrevStatus = null;
                    OrderStatus? orderNewStatus = null;
                    var invoiceBecamePaid = outcome.InvoiceBecamePaid;

                    // ─── Structured audit trail for the UAT override handling ─
                    // Log lines preserved verbatim from the inline
                    // implementation so operator-facing telemetry is
                    // unchanged.
                    if (payment.IsTestAmountOverrideApplied && payment.Invoice is not null)
                    {
                        if (outcome.TestOverrideBlockedByProduction)
                        {
                            _logger.LogError(
                                "[PaymentOverrideApply] BLOCKED in Production — payment {PaymentNumber} carries IsTestAmountOverrideApplied=true. " +
                                "Settling at provider amount {ProviderAmount} only; invoice {InvoiceNumber} will NOT be fully paid.",
                                payment.PaymentNumber, payment.Amount, payment.Invoice.InvoiceNumber);
                        }
                        else if (outcome.TestOverrideAppliedInUat && payment.InvoiceAmountAtTime is > 0m)
                        {
                            _logger.LogWarning(
                                "[PaymentOverrideApply] UAT test override applied — settling invoice {InvoiceNumber} at full amount {InvoiceAmount} " +
                                "despite provider charge of only {ProviderAmount}. payment={PaymentNumber} env={Environment}",
                                payment.Invoice.InvoiceNumber, outcome.SettlementAmount, payment.Amount,
                                payment.PaymentNumber, _env.EnvironmentName);
                        }
                        else if (outcome.TestOverrideAppliedInUat)
                        {
                            _logger.LogWarning(
                                "[PaymentOverrideApply] InvoiceAmountAtTime missing for {PaymentNumber}; falling back to Invoice.TotalAmount={InvoiceTotal} in UAT.",
                                payment.PaymentNumber, outcome.SettlementAmount);
                        }
                    }

                    // ─── Apply invoice + order mutations from the outcome ─
                    if (payment.Invoice is not null && outcome.PaidDelta != 0m)
                    {
                        payment.Invoice.AmountPaid = outcome.NewInvoiceAmountPaid;
                        payment.Invoice.BalanceDue = outcome.NewInvoiceBalanceDue;
                        payment.Invoice.Status = outcome.NewInvoiceStatus;
                        if (outcome.ShouldSetInvoicePaidAtUtc && payment.Invoice.PaidAtUtc is null)
                            payment.Invoice.PaidAtUtc = now;
                        if (outcome.ShouldClearInvoicePaidAtUtc)
                            payment.Invoice.PaidAtUtc = null;
                    }

                    if (outcome.NewOrderStatus is OrderStatus targetOrderStatus
                        && payment.Invoice?.Order is not null)
                    {
                        var order = payment.Invoice.Order;
                        orderPrevStatus = order.Status;
                        order.Status = targetOrderStatus;
                        order.LastStatusChangedByUserId = _currentUser.UserId;
                        orderNewStatus = targetOrderStatus;

                        if (outcome.ShouldStampOrderActivatedAtUtc && order.ActivatedAtUtc is null)
                            order.ActivatedAtUtc = now;
                        if (outcome.ShouldStampOrderBillingAnchorDateUtc && order.BillingAnchorDateUtc is null)
                            order.BillingAnchorDateUtc = now;

                        if (outcome.OrderAutoActivated)
                        {
                            committedOrderAutoActivated = true;
                            _logger.LogInformation(
                                "[OrderLifecycle] {OrderNumber} PendingPayment → Active (invoice {InvoiceNumber} paid; auto-activation, manual Openserve activation disabled)",
                                order.OrderNumber, payment.Invoice.InvoiceNumber);
                        }
                        else if (targetOrderStatus == OrderStatus.PendingActivation)
                        {
                            _logger.LogInformation(
                                "[OrderLifecycle] {OrderNumber} PendingPayment → PendingActivation (invoice {InvoiceNumber} paid; manual Openserve activation required)",
                                order.OrderNumber, payment.Invoice.InvoiceNumber);
                        }
                    }

                    if (outcome.NewOrderNextPayDateUtc is DateTime nextPay
                        && payment.Invoice?.Order is not null)
                    {
                        var order = payment.Invoice.Order;
                        var previousNextPay = order.NextPayDateUtc;
                        order.NextPayDateUtc = nextPay;
                        _logger.LogInformation(
                            "[OrderLifecycle] {OrderNumber} NextPayDateUtc advanced {Previous:o} → {Next:o} (paidAt={PaidAt:o})",
                            order.OrderNumber, previousNextPay, order.NextPayDateUtc, payment.PaidAtUtc);
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
            // Two flavours depending on whether the monthly invoice that
            // just paid auto-activated the order:
            //   - auto-activated (RequireManualOpenserveActivation=false):
            //     ProvisionForOrderAsync flips the Pending NetworkAccount
            //     to Active so the service tile, billing dashboard and
            //     "Active Services" KPI all agree.
            //   - not auto-activated (manual flow OR installation-fee
            //     payment): EnsurePending creates a Pending placeholder
            //     if one doesn't exist, then leaves it for the admin
            //     "Activate Service" action.
            if (committedOrderAutoActivated)
            {
                await TryActivateNetworkAccountAsync(hookOrderId, committedPayment.PaymentNumber, cancellationToken);
            }
            else
            {
                await TryProvisionNetworkAccountAsync(hookOrderId, committedPayment.PaymentNumber, cancellationToken);
            }
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

        // Phase 0B — anchor the recurring billing schedule the first time a
        // service-fee invoice is paid. Runs AFTER the network-account hook so
        // the NetworkAccount exists. Idempotent + best-effort (the service
        // swallows its own errors); creates NOTHING for non-service invoices
        // or when a schedule already exists. Does not charge/retry/suspend.
        if (committedHookInvoiceId is Guid scheduleInvoiceId)
        {
            await _billingSchedule.EnsureActivatedForPaidServiceInvoiceAsync(
                scheduleInvoiceId,
                committedPayment.PaidAtUtc ?? DateTime.UtcNow,
                cancellationToken);
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
                // Payment receipts come from the Payments mailbox so
                // replies (refund queries, "I didn't make this payment")
                // reach the right team.
                SenderType = EmailSenderType.Payments,
                RelatedEntityType = nameof(Invoice),
                RelatedEntityId = invoice.Id
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch InvoicePaid notification for invoice {InvoiceId}", invoice.Id);
        }
    }

    // Go-live lifecycle: when ANY invoice tied to an order becomes
    // Paid, ensure a Pending NetworkAccount row exists for that order
    // — but DO NOT activate. Activation is gated behind the admin
    // "Mark service activated on Openserve" action because the
    // Openserve provider has no API today and an admin must manually
    // activate the line before SmartFuture flips the service Active.
    //
    // EnsurePendingForOrderAsync is idempotent (skips if any non-
    // terminated NetworkAccount already exists), so this is safe to
    // call on install-fee invoice paid AND on monthly invoice paid —
    // the second call is a no-op.
    private async Task TryProvisionNetworkAccountAsync(Guid orderId, string paymentNumber, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _networkAccountService.EnsurePendingForOrderAsync(
                orderId, NetworkAccountSource.SystemAutomated, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogInformation(
                    "[NetworkAccountEnsurePending] (Payment {PaymentNumber}) skipped: {Code} {Message}",
                    paymentNumber, result.Code, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[NetworkAccountEnsurePending] (Payment {PaymentNumber}) threw",
                paymentNumber);
        }
    }

    // Auto-activation companion to TryProvisionNetworkAccountAsync.
    // Fires only when the monthly invoice paid AND the system is
    // configured to skip the manual Openserve step
    // (ServiceActivation:RequireManualOpenserveActivation=false). Calls
    // the existing ProvisionForOrderAsync which flips the Pending row
    // to Active, runs the provisioner, and emits the audit log.
    // Idempotent — re-running it after a successful activation returns
    // "already active".
    private async Task TryActivateNetworkAccountAsync(Guid orderId, string paymentNumber, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _networkAccountService.ProvisionForOrderAsync(
                orderId, NetworkAccountSource.SystemAutomated, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "[NetworkAccountAutoActivate] (Payment {PaymentNumber}) returned non-success: {Code} {Message}",
                    paymentNumber, result.Code, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[NetworkAccountAutoActivate] (Payment {PaymentNumber}) threw — order is Active in DB but NetworkAccount may still be Pending; admin can re-run Activate Service.",
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

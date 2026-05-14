using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<PaymentApplierService> _logger;

    public PaymentApplierService(IAppDbContext dbContext, IAuditService auditService, INotificationService notificationService, INetworkAccountService networkAccountService, ICurrentUserService currentUser,
        ILogger<PaymentApplierService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _notificationService = notificationService;
        _networkAccountService = networkAccountService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<PaymentDto>> ApplyStatusChangeAsync(ApplyPaymentStatusChangeRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return Result<PaymentDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

        if (request.PaymentId == Guid.Empty)
            return Result<PaymentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "PaymentId is required.");

        await using var transaction = await _dbContext.BeginTransactionAsync(cancellationToken);

        try
        {
            var payment = await _dbContext.Payments
                .Include(p => p.Invoice).ThenInclude(i => i!.Order)
                .Include(p => p.LastStatusChangedByUser)
                .FirstOrDefaultAsync(p => p.Id == request.PaymentId, cancellationToken);

            if (payment is null)
                return Result<PaymentDto>.Failure(ErrorCodes.NOT_FOUND, "Payment not found.");

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
                return Result<PaymentDto>.Success(
                    MapToDto(payment),
                    "Payment status unchanged; gateway references refreshed.");
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
                var delta = isCompleted ? payment.Amount : -payment.Amount;
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
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            // Audit + notification side effects. Audit failures must not break the flow,
            // but they're inside the transaction so a SaveChanges failure rolls everything back.
            await EmitPaymentAuditAsync(payment, previous, newStatus);

            if (orderPrevStatus.HasValue && orderNewStatus.HasValue
                && payment.Invoice?.Order is not null)
            {
                await EmitOrderStatusChangedAuditAsync(
                    payment.Invoice.Order, orderPrevStatus.Value, orderNewStatus.Value, payment.PaymentNumber);
            }

            await transaction.CommitAsync(cancellationToken);

            // Notification happens *after* commit. Notification dispatch can be slow and
            // a notification failure should never undo a successful payment application.
            if (invoiceBecamePaid && request.TriggerNotifications && payment.Invoice is not null)
            {
                await TryNotifyInvoicePaidAsync(payment.Invoice, payment, cancellationToken);
            }

            // Auto-provisioning hook for non-physical packages whose orders just became
            // post-payment. NetworkAccountService applies its own eligibility checks, so
            // physical-package orders will be no-ops here.
            if (invoiceBecamePaid && payment.Invoice?.OrderId is Guid invoiceOrderId)
            {
                await TryProvisionNetworkAccountAsync(invoiceOrderId, payment.PaymentNumber, cancellationToken);
            }

            return Result<PaymentDto>.Success(MapToDto(payment), "Payment status applied.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error applying payment status change {PaymentId}", request.PaymentId);
            try { await transaction.RollbackAsync(cancellationToken); } catch { /* swallow */ }
            return Result<PaymentDto>.Failure(
                ErrorCodes.EXCEPTION,
                "An unexpected error occurred while applying the payment status change.");
        }
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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Communication;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Decides which Openserve status transitions are worth telling a
/// customer about (brief Priority 6) and dispatches through the
/// existing OutboundNotification pipeline — no new notification-history
/// table, no fabricated "sent" status for unconfigured channels (SMS/
/// WhatsApp genuinely attempt delivery via ISmsProvider/IWhatsAppProvider
/// and record whatever comes back, including PROVIDER_NOT_CONFIGURED).
///
/// Deliberately NOT every technical event: only transitions a customer
/// would recognise as progress on their order fire a message.
/// </summary>
public class OpenserveCustomerNotificationService : IOpenserveCustomerNotificationService
{
    private const string SmartFutureSupportLine = "Need help? Contact SmartFuture support at support@smartfuture.co.za.";

    private readonly IAppDbContext _dbContext;
    private readonly INotificationService _notificationService;
    private readonly ILogger<OpenserveCustomerNotificationService> _logger;

    public OpenserveCustomerNotificationService(
        IAppDbContext dbContext, INotificationService notificationService, ILogger<OpenserveCustomerNotificationService> logger)
    {
        _dbContext = dbContext;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<bool> NotifyStatusChangedAsync(
        OpenserveOrder openserveOrder, Order order,
        OpenserveProvisioningStatus previous, OpenserveProvisioningStatus current,
        CancellationToken cancellationToken = default)
    {
        if (previous == current) return false;

        var content = BuildContent(previous, current, order);
        if (content is null) return false; // not a customer-meaningful transition

        return await DispatchAsync(order, content.Value.type, content.Value.subject, content.Value.body, cancellationToken);
    }

    public async Task<bool> NotifyServiceActivatedAsync(Order order, CancellationToken cancellationToken = default)
    {
        var subject = $"Your SmartFuture fibre service is now active — {order.OrderNumber}";
        var body =
            $"Hi {FirstNameOrFallback(order.FullName)},\n\n" +
            $"Great news — your SmartFuture fibre service (order {order.OrderNumber}) is now active and ready to use.\n\n" +
            $"{SmartFutureSupportLine}";

        return await DispatchAsync(order, NotificationType.OpenserveServiceActivated, subject, body, cancellationToken);
    }

    private static (NotificationType type, string subject, string body)? BuildContent(
        OpenserveProvisioningStatus previous, OpenserveProvisioningStatus current, Order order)
    {
        var name = FirstNameOrFallback(order.FullName);
        var orderRef = order.OrderNumber;

        return current switch
        {
            OpenserveProvisioningStatus.Submitted when previous is OpenserveProvisioningStatus.NotSubmitted or OpenserveProvisioningStatus.Submitting =>
                (NotificationType.OpenserveOrderReceived,
                 $"Your fibre order has been received — {orderRef}",
                 $"Hi {name},\n\nOpenserve has received your fibre order ({orderRef}) and it's now being processed.\n\nWe'll keep you updated as it progresses.\n\n{SmartFutureSupportLine}"),

            OpenserveProvisioningStatus.InProgress =>
                (NotificationType.OpenserveOrderProcessing,
                 $"Your fibre order is being processed — {orderRef}",
                 $"Hi {name},\n\nYour fibre order ({orderRef}) is now being processed for installation.\n\n{SmartFutureSupportLine}"),

            OpenserveProvisioningStatus.AwaitingCancellation =>
                (NotificationType.OpenserveActionRequired,
                 $"An update on your fibre order — {orderRef}",
                 $"Hi {name},\n\nWe need to verify some information regarding your fibre order ({orderRef}) before it can continue. Our team is on it.\n\n{SmartFutureSupportLine}"),

            OpenserveProvisioningStatus.Completed =>
                (NotificationType.OpenserveInstallationComplete,
                 $"Your fibre order has been completed — {orderRef}",
                 $"Hi {name},\n\nOpenserve has completed your fibre order ({orderRef}). We're finalising activation on our side and will confirm shortly.\n\n{SmartFutureSupportLine}"),

            OpenserveProvisioningStatus.Cancelled =>
                (NotificationType.OpenserveOrderCancelled,
                 $"Your fibre order has been cancelled — {orderRef}",
                 $"Hi {name},\n\nYour fibre order ({orderRef}) has been cancelled. If this wasn't expected, please get in touch.\n\n{SmartFutureSupportLine}"),

            _ => null // Unknown / no customer-safe copy for an unrecognised Openserve state
        };
    }

    private async Task<bool> DispatchAsync(Order order, NotificationType type, string subject, string body, CancellationToken cancellationToken)
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

            if (contact is null) return false;

            var dispatchedAny = false;

            if (!string.IsNullOrWhiteSpace(contact.Email))
            {
                await _notificationService.SendAsync(new SendNotificationRequestDto
                {
                    UserId = contact.UserId,
                    Channel = NotificationChannel.Email,
                    Type = type,
                    RecipientEmail = contact.Email,
                    RecipientPhone = contact.Phone,
                    Subject = subject,
                    Body = body,
                    SenderType = EmailSenderType.Default,
                    RelatedEntityType = nameof(Order),
                    RelatedEntityId = order.Id
                }, cancellationToken);
                dispatchedAny = true;
            }

            // SMS/WhatsApp: attempted for real via ISmsProvider/
            // IWhatsAppProvider through NotificationService — recorded
            // as Failed with PROVIDER_NOT_CONFIGURED while no real
            // provider is wired up, never faked as delivered. Only
            // attempted when a phone number exists.
            if (!string.IsNullOrWhiteSpace(contact.Phone))
            {
                await _notificationService.SendAsync(new SendNotificationRequestDto
                {
                    UserId = contact.UserId,
                    Channel = NotificationChannel.Sms,
                    Type = type,
                    RecipientPhone = contact.Phone,
                    Body = body,
                    RelatedEntityType = nameof(Order),
                    RelatedEntityId = order.Id
                }, cancellationToken);
                dispatchedAny = true;
            }

            return dispatchedAny;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch Openserve {Type} notification for order {OrderId}", type, order.Id);
            return false;
        }
    }

    private static string FirstNameOrFallback(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "there";
        var first = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? "there" : first;
    }
}

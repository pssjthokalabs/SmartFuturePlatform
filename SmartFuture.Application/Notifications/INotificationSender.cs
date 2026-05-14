using SmartFuture.Application.Notifications.Dtos;

namespace SmartFuture.Application.Notifications;

public interface INotificationSender
{
    Task<NotificationSendResult> SendAsync(
        SendNotificationRequestDto request,
        CancellationToken cancellationToken = default);
}

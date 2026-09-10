namespace SmartFuture.Shared.Enums.Notifications;

public enum NotificationChannel
{
    Email = 0,
    Sms = 1,
    Push = 2,
    System = 3,

    // Additive — no DB check constraint on OutboundNotification.Channel.
    WhatsApp = 4
}

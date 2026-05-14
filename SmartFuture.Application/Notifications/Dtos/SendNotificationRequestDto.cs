using SmartFuture.Shared.Enums.Notifications;

namespace SmartFuture.Application.Notifications.Dtos;

public class SendNotificationRequestDto
{
    public Guid? UserId { get; set; }
    public NotificationChannel Channel { get; set; }
    public NotificationType Type { get; set; }

    public string? RecipientEmail { get; set; }
    public string? RecipientPhone { get; set; }

    public string? Subject { get; set; }
    public string Body { get; set; } = string.Empty;

    public string? RelatedEntityType { get; set; }
    public Guid? RelatedEntityId { get; set; }
    public string? MetadataJson { get; set; }
}

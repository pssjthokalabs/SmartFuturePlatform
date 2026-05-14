using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Notifications;

namespace SmartFuture.Domain.Notifications;

public class OutboundNotification : BaseEntity
{
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public NotificationChannel Channel { get; set; }
    public NotificationType Type { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    public string? RecipientEmail { get; set; }
    public string? RecipientPhone { get; set; }

    public string? Subject { get; set; }
    public string Body { get; set; } = string.Empty;

    public string? ProviderName { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? FailureReason { get; set; }

    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }

    public string? RelatedEntityType { get; set; }
    public Guid? RelatedEntityId { get; set; }

    public string? MetadataJson { get; set; }
}

using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Notifications.Dtos;

public class NotificationFilterRequestDto : PagedListQueryBase
{
    public Guid? UserId { get; set; }
    public NotificationChannel? Channel { get; set; }
    public NotificationType? Type { get; set; }
    public new NotificationStatus? Status { get; set; }
    public string? RecipientEmail { get; set; }
    public string? RecipientPhone { get; set; }
    public string? RelatedEntityType { get; set; }
    public Guid? RelatedEntityId { get; set; }
    public DateTime? SentFromUtc { get; set; }
    public DateTime? SentToUtc { get; set; }
}

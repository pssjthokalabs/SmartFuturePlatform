using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Domain.Jobs;

// One row per alert-send attempt per subscriber. Also written for
// Skipped sends (nothing matched / module disabled) so the admin can
// answer "why no email" without guessing.
public class JobAlertDeliveryLog : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public JobAlertDeliveryStatus Status { get; set; } = JobAlertDeliveryStatus.Pending;
    public JobAlertFrequency Frequency { get; set; }

    // Window the digest covered. Both inclusive-start / exclusive-end.
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }

    public int JobCount { get; set; }
    public string? RecipientEmail { get; set; }
    public string? Subject { get; set; }
    public string? FailureMessage { get; set; }
    public DateTime? SentAtUtc { get; set; }

    // Links back to the OutboundNotification row the existing
    // notification pipeline created, when one was queued.
    public Guid? NotificationId { get; set; }
}

using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Domain.Jobs;

// Newsletter/alert opt-in for a job subscriber. One row per user.
// Separate from JobSubscriberProfile on purpose: a user can keep a
// complete profile while switching alerts off, and the alert worker
// only ever reads this small table.
public class JobAlertPreference : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public bool IsSubscribed { get; set; } = true;
    public JobAlertFrequency Frequency { get; set; } = JobAlertFrequency.Weekly;

    // Null/empty = "everything". JSON string arrays, same convention as
    // the profile's preference fields.
    public string? CategoriesJson { get; set; }
    public string? LocationsJson { get; set; }
    public string? KeywordsJson { get; set; }

    public DateTime? LastSentAtUtc { get; set; }
    public DateTime? UnsubscribedAtUtc { get; set; }

    // Opaque token embedded in the alert email's unsubscribe link so a
    // one-click unsubscribe needs no login.
    public string? UnsubscribeToken { get; set; }
}

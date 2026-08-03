using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Dtos;

public class JobAlertPreferenceDto
{
    public Guid UserId { get; set; }
    public bool IsSubscribed { get; set; }
    public JobAlertFrequency Frequency { get; set; }
    public string FrequencyLabel { get; set; } = string.Empty;
    public IReadOnlyList<string> Categories { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Locations { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Keywords { get; set; } = Array.Empty<string>();
    public DateTime? LastSentAtUtc { get; set; }
    public DateTime? UnsubscribedAtUtc { get; set; }
}

public class UpdateJobAlertPreferenceRequestDto
{
    public bool? IsSubscribed { get; set; }
    public JobAlertFrequency? Frequency { get; set; }
    public List<string>? Categories { get; set; }
    public List<string>? Locations { get; set; }
    public List<string>? Keywords { get; set; }
}

public class JobAlertDeliveryLogDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? RecipientEmail { get; set; }
    public JobAlertDeliveryStatus Status { get; set; }
    public string StatusLabel { get; set; } = string.Empty;
    public JobAlertFrequency Frequency { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }
    public int JobCount { get; set; }
    public string? Subject { get; set; }
    public string? FailureMessage { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// Result of one digest run — what the admin sees after pressing
// "Send job alerts now" (and what the scheduled worker logs).
public class JobAlertRunSummaryDto
{
    public bool AlertsEnabled { get; set; }
    public int SubscribersConsidered { get; set; }
    public int EmailsQueued { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }
    public string? Message { get; set; }
}

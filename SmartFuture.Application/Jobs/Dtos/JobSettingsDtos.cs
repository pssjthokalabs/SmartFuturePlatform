namespace SmartFuture.Application.Jobs.Dtos;

public class JobSettingsDto
{
    public bool JobDetailsSubscribersOnly { get; set; }
    public bool JobAlertsEnabled { get; set; }
    public bool JobsModuleEnabled { get; set; }
    public bool AutoImportEnabled { get; set; }
    public int ImportIntervalMinutes { get; set; }
    public int ExpiredJobRetentionDays { get; set; }
    public string? PublicDisclaimer { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

// Null = leave unchanged, so the admin form can submit one toggle.
public class UpdateJobSettingsRequestDto
{
    public bool? JobDetailsSubscribersOnly { get; set; }
    public bool? JobAlertsEnabled { get; set; }
    public bool? JobsModuleEnabled { get; set; }
    public bool? AutoImportEnabled { get; set; }
    public int? ImportIntervalMinutes { get; set; }
    public int? ExpiredJobRetentionDays { get; set; }
    public string? PublicDisclaimer { get; set; }
}

// Tiny public payload so the website/app can render the module shell
// (or hide it) without an admin token.
public class PublicJobSettingsDto
{
    public bool JobsModuleEnabled { get; set; }
    public bool JobDetailsSubscribersOnly { get; set; }
    public bool JobAlertsEnabled { get; set; }
    public string? PublicDisclaimer { get; set; }
}

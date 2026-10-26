namespace SmartFuture.Application.Jobs.Import;

/// <summary>
/// When the once-daily scheduled job import runs and how it retries.
///
/// Every value has a production default, so nothing has to be configured
/// for the schedule to work — the section exists only so the run time can
/// be moved without a code change. Invalid values fall back to the
/// defaults (see <see cref="JobImportDailySchedule"/>) rather than stopping
/// the importer.
///
/// Whether the import runs AT ALL is not decided here: that stays the
/// DB-backed JobModuleSettings switches (JobsModuleEnabled and
/// AutoImportEnabled), which an admin controls from Job Settings.
/// </summary>
public class JobImportScheduleSettings
{
    public const string SectionName = "JobImportSchedule";

    /// <summary>Local wall-clock time of the daily run, "HH:mm".</summary>
    public string DailyRunTime { get; set; } = "05:00";

    /// <summary>
    /// IANA id. South Africa has no daylight saving, so 05:00 SAST is
    /// always 03:00 UTC.
    /// </summary>
    public string TimeZone { get; set; } = "Africa/Johannesburg";

    /// <summary>Additional attempts after the first one fails. Capped at 5.</summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Delay before the first retry; doubles for each later retry.</summary>
    public int RetryBaseDelayMinutes { get; set; } = 5;

    /// <summary>Upper bound on any single retry delay.</summary>
    public int RetryMaxDelayMinutes { get; set; } = 60;
}

namespace SmartFuture.Shared.Enums.Jobs;

public enum JobImportRunTrigger
{
    // Admin pressed "Refresh now" on a source or on the jobs dashboard.
    Manual = 0,
    // Background worker tick (per-source CrawlFrequencyMinutes).
    Scheduled = 1
}

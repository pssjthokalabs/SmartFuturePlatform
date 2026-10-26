namespace SmartFuture.Shared.Enums.Jobs;

public enum JobImportRunTrigger
{
    // Admin pressed "Refresh now" on a source or on the jobs dashboard.
    Manual = 0,
    // Background worker crawl of one source — either inside the daily
    // scheduled import or on the source's own CrawlFrequencyMinutes.
    Scheduled = 1,
    // Summary row for ONE attempt of the once-daily scheduled import
    // (SourceId is null). The per-source rows it starts carry Scheduled.
    // Stored as an int, so adding this value needs no migration.
    DailySchedule = 2
}

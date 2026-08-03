namespace SmartFuture.Shared.Enums.Jobs;

public enum JobImportRunStatus
{
    Running = 0,
    Succeeded = 1,
    // At least one source succeeded and at least one failed. The run is
    // NOT treated as a failure — partial imports are the normal steady
    // state when one board blocks crawling.
    PartiallySucceeded = 2,
    Failed = 3
}

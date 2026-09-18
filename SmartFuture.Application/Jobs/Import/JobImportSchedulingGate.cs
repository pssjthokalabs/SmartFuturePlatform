using SmartFuture.Domain.Jobs;

namespace SmartFuture.Application.Jobs.Import;

// The exact predicate JobImportHostedService.RunTickAsync gates the
// scheduled crawl loop on, extracted so it's directly unit-testable
// without spinning up a BackgroundService. Re-read every tick (the
// hosted service re-fetches JobModuleSettings each time via
// IJobSettingsService.GetOrCreateAsync), so flipping either flag off
// takes effect on the very next tick — no restart needed.
public static class JobImportSchedulingGate
{
    public static bool ShouldRunScheduledImport(JobModuleSettings settings)
        => settings.JobsModuleEnabled && settings.AutoImportEnabled;
}

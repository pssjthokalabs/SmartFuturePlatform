using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.API.HostedServices;

/// <summary>
/// Scheduled job-source crawler. Modelled on
/// <see cref="ExpiredRefreshTokenCleanupHostedService"/>: scoped per run,
/// tolerant of transient failures, never crashes the host.
///
/// SAFETY:
///   • Gated by the DB-backed <c>JobModuleSettings.AutoImportEnabled</c>
///     flag, which defaults to FALSE. Deploying this module therefore
///     never starts crawling the internet by surprise — an admin has to
///     turn it on from Job Settings.
///   • The flag is re-read every tick, so it can be switched off without
///     a restart.
///   • Only sources that are Active AND have their own
///     <c>CrawlFrequencyMinutes</c> set are eligible (<c>onlyDue: true</c>).
///     A source with no interval stays manual-refresh only.
///   • Per-source failures are swallowed by the import service and land
///     in the JobImportRuns log — one blocked board never stops the rest.
/// </summary>
public class JobImportHostedService : BackgroundService
{
    // Let the app finish starting (and migrations run) before the first
    // outbound crawl.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    // How often we WAKE UP. What actually gets crawled on each wake-up is
    // decided per source by its own CrawlFrequencyMinutes.
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobImportHostedService> _logger;

    public JobImportHostedService(IServiceScopeFactory scopeFactory, ILogger<JobImportHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The worker must survive anything — including the
                // database being briefly unreachable.
                _logger.LogError(ex, "[job-import][hosted_service] tick failed; will retry on the next interval.");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunTickAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IJobSettingsService>();

        var settings = await settingsService.GetOrCreateAsync(stoppingToken);
        if (!settings.JobsModuleEnabled || !settings.AutoImportEnabled)
        {
            // Quiet by design — this is the DEFAULT state and logging it
            // at Information every 15 minutes would be noise.
            _logger.LogDebug("[job-import][hosted_service] skipped — module={Module} autoImport={AutoImport}",
                settings.JobsModuleEnabled, settings.AutoImportEnabled);
            return;
        }

        var importService = scope.ServiceProvider.GetRequiredService<IJobImportService>();
        var result = await importService.RunAllAsync(JobImportRunTrigger.Scheduled, onlyDue: true, stoppingToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("[job-import][hosted_service] run failed: {Message}", result.Message);
            return;
        }

        var summary = result.Data;
        if (summary is null || summary.SourcesAttempted == 0) return;

        _logger.LogInformation(
            "[job-import][hosted_service] attempted={Attempted} succeeded={Succeeded} failed={Failed} created={Created} updated={Updated} expired={Expired}",
            summary.SourcesAttempted, summary.SourcesSucceeded, summary.SourcesFailed,
            summary.JobsCreated, summary.JobsUpdated, summary.JobsExpired);
    }
}

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
    private readonly IJobImportQueue _queue;
    private readonly ILogger<JobImportHostedService> _logger;

    public JobImportHostedService(IServiceScopeFactory scopeFactory, IJobImportQueue queue, ILogger<JobImportHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>
    /// Two independent loops.
    ///
    /// The SCHEDULED loop is unchanged: startup delay, then a tick every
    /// 15 minutes honouring each source's CrawlFrequencyMinutes, gated by
    /// AutoImportEnabled.
    ///
    /// The QUEUE loop is new and deliberately NOT gated by
    /// AutoImportEnabled, has no startup delay, and does not wait for a
    /// tick: an admin who clicks Refresh has explicitly asked for this
    /// one source right now, which is a different thing from letting the
    /// crawler run unattended. It blocks on the channel, so a queued
    /// refresh starts within milliseconds.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            RunScheduledLoopAsync(stoppingToken),
            RunQueueLoopAsync(stoppingToken));
    }

    private async Task RunScheduledLoopAsync(CancellationToken stoppingToken)
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

    // Drains manual refreshes queued by POST /api/admin/job-sources/{id}/refresh.
    // One at a time on purpose: crawling is outbound HTTP against other
    // people's servers, and running several at once is both rude and a
    // good way to get the whole host blocked.
    private async Task RunQueueLoopAsync(CancellationToken stoppingToken)
    {
        // Clear anything orphaned by the restart that just happened,
        // before the first refresh is blocked by a stale Running row.
        await ReapOnStartupAsync(stoppingToken);

        try
        {
            await foreach (var item in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var importService = scope.ServiceProvider.GetRequiredService<IJobImportService>();

                    _logger.LogInformation("[job-import][queue] starting run {RunId} for source {SourceId}", item.RunId, item.SourceId);
                    await importService.ExecuteQueuedRunAsync(item.SourceId, item.RunId, stoppingToken);
                    _logger.LogInformation("[job-import][queue] finished run {RunId}", item.RunId);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // ExecuteQueuedRunAsync already swallows and records
                    // per-run failures; anything reaching here is a
                    // scope/DI level fault. The loop must survive it or a
                    // single bad run kills every later refresh.
                    _logger.LogError(ex, "[job-import][queue] run {RunId} threw outside the import service.", item.RunId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task ReapOnStartupAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var importService = scope.ServiceProvider.GetRequiredService<IJobImportService>();
            await importService.ReapStuckRunsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[job-import][queue] startup reap failed; continuing.");
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

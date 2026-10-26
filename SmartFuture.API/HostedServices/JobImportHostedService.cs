using SmartFuture.Application.Jobs.Import;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.API.HostedServices;

/// <summary>
/// Scheduled job-source crawler. Modelled on
/// <see cref="ExpiredRefreshTokenCleanupHostedService"/>: scoped per run,
/// tolerant of transient failures, never crashes the host.
///
/// Each wake-up does two things, in order:
///   1. The DAILY IMPORT (<see cref="IJobImportScheduler"/>): once a day at
///      05:00 SAST every scheduled source is refreshed, with up to 5
///      retries for sources that failed transiently. Claimed in the
///      database, so a restart or a second API instance never runs it twice.
///      If the API was asleep at 05:00 it runs as soon as the API wakes.
///   2. The existing PER-SOURCE INTERVAL crawl, unchanged: a source set to
///      refresh more often than daily still gets its extra crawls. Skipped
///      while a daily attempt is running anywhere, so the two never overlap.
///
/// SAFETY:
///   • Gated by the DB-backed <c>JobModuleSettings.AutoImportEnabled</c>
///     flag, which defaults to FALSE. Deploying this module therefore
///     never starts crawling the internet by surprise — an admin has to
///     turn it on from Job Settings.
///   • The flag is re-read every tick, so it can be switched off without
///     a restart.
///   • Only sources that are Active AND have their own
///     <c>CrawlFrequencyMinutes</c> set are eligible. A source with no
///     interval stays manual-refresh only.
///   • Per-source failures are swallowed by the import service and land
///     in the JobImportRuns log — one blocked board never stops the rest.
///
/// HOSTING: this is in-process, so it only runs while the API process is
/// alive. On IIS, an idle app pool is shut down; the daily import then
/// runs on the first wake-up after 05:00 instead of at 05:00. Keep the
/// pool warm (AlwaysRunning + preload, or an uptime ping) for on-time runs.
/// </summary>
public class JobImportHostedService : BackgroundService
{
    // Let the app finish starting (and migrations run) before the first
    // outbound crawl.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    // The longest we ever sleep. Shorter when a daily retry is due sooner.
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    // The shortest we ever sleep, so a clock edge can never spin the loop.
    private static readonly TimeSpan MinimumTickDelay = TimeSpan.FromSeconds(30);

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
    /// The SCHEDULED loop: startup delay, then a tick at most every 15
    /// minutes (sooner when a daily retry is due) running the daily import
    /// and then each source's own CrawlFrequencyMinutes, gated by
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
            var delay = TickInterval;
            try
            {
                delay = await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The worker must survive anything — including the
                // database being briefly unreachable. Nothing was claimed,
                // so the daily import simply runs on the next tick.
                _logger.LogError(ex, "[job-import][hosted_service] tick failed; will retry on the next interval.");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
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

    // Returns how long to sleep before the next tick.
    private async Task<TimeSpan> RunTickAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IJobImportScheduler>();

        // The daily import checks the AutoImportEnabled/JobsModuleEnabled
        // gate itself and reports Disabled when either is off.
        var daily = await scheduler.RunDueAsync(DateTime.UtcNow, stoppingToken);
        if (daily.State == JobImportScheduleState.Disabled)
        {
            // Quiet by design — this is the DEFAULT state and logging it
            // at Information every 15 minutes would be noise.
            _logger.LogDebug("[job-import][hosted_service] skipped — automatic import is switched off.");
            return TickInterval;
        }

        // Another instance is mid-attempt: crawling on the interval path
        // now could touch the very sources it is importing.
        if (daily.State != JobImportScheduleState.Running)
            await RunIntervalCrawlsAsync(scope.ServiceProvider, stoppingToken);

        return NextTickDelay(daily.NextCheckUtc, DateTime.UtcNow);
    }

    // The pre-existing per-source path: sources whose own
    // CrawlFrequencyMinutes has elapsed. After the daily import has run,
    // a daily-or-slower source is not due again, so in practice this only
    // adds the extra crawls of sources configured more often than daily.
    private async Task RunIntervalCrawlsAsync(IServiceProvider services, CancellationToken stoppingToken)
    {
        var importService = services.GetRequiredService<IJobImportService>();
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

    /// <summary>
    /// Sleep until the scheduler's next decision point (e.g. a retry's
    /// backoff), but never longer than <see cref="TickInterval"/> — the
    /// interval crawl and the on/off switch are re-checked at least that
    /// often — and never shorter than <see cref="MinimumTickDelay"/>.
    /// </summary>
    public static TimeSpan NextTickDelay(DateTime nextCheckUtc, DateTime nowUtc)
    {
        var until = nextCheckUtc - nowUtc;
        if (until < MinimumTickDelay) return MinimumTickDelay;
        return until > TickInterval ? TickInterval : until;
    }
}

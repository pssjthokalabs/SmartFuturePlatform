using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.API.HostedServices;

/// <summary>
/// Phase 0A — daily recurring-billing worker SHELL. Modelled on
/// <see cref="ExpiredRefreshTokenCleanupHostedService"/>: scoped per run,
/// tolerant of transient failures, never crashes the host.
///
/// SAFETY (Phase 0A):
///   • Disabled by default (<c>AutoBilling__RecurringWorkerEnabled=false</c>)
///     — the loop never starts.
///   • Dry-run by default (<c>AutoBilling__DryRun=true</c>).
///   • Every orchestrator stage is a no-op — no invoice generation, no
///     charging, no retries, no suspension. The only effect of a run is a
///     <c>BillingRunLog</c> row.
/// </summary>
public class RecurringBillingHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AutoBillingSettings> _settings;
    private readonly ILogger<RecurringBillingHostedService> _logger;

    public RecurringBillingHostedService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AutoBillingSettings> settings,
        ILogger<RecurringBillingHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.RecurringWorkerEnabled)
        {
            _logger.LogInformation(
                "[recurring-billing][hosted_service] disabled — AutoBilling__RecurringWorkerEnabled=false. Worker will not run.");
            return;
        }

        _logger.LogInformation(
            "[recurring-billing][hosted_service] enabled — runHour={Hour:D2}:{Minute:D2} UTC dryRun={DryRun} " +
            "preventConcurrentRuns={Lock} suspendAfterGrace={Suspend}",
            settings.DailyRunHour, settings.DailyRunMinute, settings.DryRun,
            settings.PreventConcurrentRuns, settings.SuspendAfterGracePeriodEnabled);

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
            var current = _settings.CurrentValue;

            // Re-check the master switch each cycle so it can be turned off
            // without a restart (the loop simply idles until next day).
            if (!current.RecurringWorkerEnabled)
            {
                _logger.LogInformation(
                    "[recurring-billing][hosted_service] master switch turned off; idling until re-enabled.");
            }
            else
            {
                try
                {
                    await RunOnceAsync(current, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[recurring-billing][hosted_service] run tick threw; will retry at the next scheduled time.");
                }
            }

            var delay = ComputeDelayUntilNextRun(DateTime.UtcNow, current.DailyRunHour, current.DailyRunMinute);
            _logger.LogInformation(
                "[recurring-billing][hosted_service] next run in {Hours:0.0}h at {Hour:D2}:{Minute:D2} UTC.",
                delay.TotalHours, current.DailyRunHour, current.DailyRunMinute);
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

    private async Task RunOnceAsync(AutoBillingSettings settings, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IRecurringBillingOrchestrator>();

        var context = new RecurringBillingRunContext(
            RunId: Guid.NewGuid(),
            NowUtc: DateTime.UtcNow,
            DryRun: settings.DryRun,
            TriggeredBy: BillingRunTrigger.Scheduler,
            MaxInvoicesPerRun: settings.MaxInvoicesPerRun,
            MaxChargesPerRun: settings.MaxChargesPerRun);

        await orchestrator.RunAsync(context, cancellationToken);
    }

    /// <summary>
    /// Delay from <paramref name="nowUtc"/> until the next occurrence of
    /// <paramref name="hour"/>:<paramref name="minute"/> UTC. If that time
    /// has already passed today, schedules for tomorrow.
    /// </summary>
    internal static TimeSpan ComputeDelayUntilNextRun(DateTime nowUtc, int hour, int minute)
    {
        var safeHour = Math.Clamp(hour, 0, 23);
        var safeMinute = Math.Clamp(minute, 0, 59);

        var todayRun = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, safeHour, safeMinute, 0, DateTimeKind.Utc);
        var nextRun = todayRun > nowUtc ? todayRun : todayRun.AddDays(1);
        return nextRun - nowUtc;
    }
}

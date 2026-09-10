using Microsoft.Extensions.Options;
using SmartFuture.Application.Openserve;

namespace SmartFuture.API.HostedServices;

/// <summary>
/// Safety-net polling for Openserve orders (brief Priority 5). Webhooks
/// (OpenserveController) are the primary update mechanism — this exists
/// because webhooks can be missed, delayed, or never delivered.
/// Modelled directly on ExpiredRefreshTokenCleanupHostedService: scoped
/// per tick, tolerant of transient failures, never crashes the host.
///
/// Gated by OpenserveFulfilment:Enabled (re-read every tick, same
/// pattern as JobImportHostedService's AutoImportEnabled check) and by
/// OpenserveFulfilment:PollingFallbackIntervalMinutes for the tick
/// interval — deliberately conservative (default 30 minutes) since this
/// is outbound HTTP against Openserve, not something to hammer.
/// Terminal orders (Completed/Cancelled) are excluded from the query
/// entirely, so a finished order stops costing any polling at all.
/// </summary>
public class OpenserveReconciliationHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<OpenserveFulfilmentSettings> _settingsMonitor;
    private readonly ILogger<OpenserveReconciliationHostedService> _logger;

    public OpenserveReconciliationHostedService(
        IServiceScopeFactory scopeFactory, IOptionsMonitor<OpenserveFulfilmentSettings> settingsMonitor,
        ILogger<OpenserveReconciliationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _settingsMonitor = settingsMonitor;
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
            var settings = _settingsMonitor.CurrentValue;

            if (settings.Enabled)
            {
                try
                {
                    var processed = await RunTickAsync(stoppingToken);
                    if (processed > 0)
                        _logger.LogInformation("[Openserve][reconcile] tick processed {Count} non-terminal order(s).", processed);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Openserve][reconcile] tick failed. Will retry at the next interval.");
                }
            }
            else
            {
                _logger.LogDebug("[Openserve][reconcile] skipped — OpenserveFulfilment:Enabled is false.");
            }

            var intervalMinutes = Math.Clamp(settings.PollingFallbackIntervalMinutes, (int)MinInterval.TotalMinutes, (int)MaxInterval.TotalMinutes);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task<int> RunTickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var reconciliation = scope.ServiceProvider.GetRequiredService<IOpenserveReconciliationService>();
        return await reconciliation.ReconcileNonTerminalOrdersAsync(cancellationToken);
    }
}

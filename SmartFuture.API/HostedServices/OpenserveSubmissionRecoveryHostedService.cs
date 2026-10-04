using SmartFuture.Application.Openserve;

namespace SmartFuture.API.HostedServices;

/// <summary>
/// Openserve SUBMISSION recovery — "SmartFuture failed to create/send the
/// order". Deliberately separate from OpenserveReconciliationHostedService
/// ("Openserve has the order — poll its status"): this worker never touches
/// an order Openserve accepted, and reconciliation never sends Create Order.
///
/// Every tick (OpenserveFulfilment:SubmissionRecovery:IntervalMinutes,
/// default 15): record interrupted claims as outcome-unknown, then resend
/// Retryable failures whose backoff has elapsed. The safety sweep (Fibre
/// orders that should have been submitted but have no record at all) runs on
/// the first tick after start-up and then every SafetySweepIntervalHours
/// (default 24, at most 24). Running it at start-up matters on IIS: an app
/// pool that recycles more often than daily would otherwise never reach a
/// 24h timer.
///
/// Gated by OpenserveFulfilment:Enabled and SubmissionRecovery:Enabled,
/// re-read every tick. Scoped per tick, never crashes the host.
/// </summary>
public class OpenserveSubmissionRecoveryHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(4);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly ILogger<OpenserveSubmissionRecoveryHostedService> _logger;
    private DateTime? _lastSweepUtc;

    public OpenserveSubmissionRecoveryHostedService(IServiceScopeFactory scopeFactory, IOpenserveRuntimeConfigProvider configProvider, ILogger<OpenserveSubmissionRecoveryHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _configProvider = configProvider;
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
            await _configProvider.RefreshAsync(stoppingToken);
            var settings = _configProvider.Current;
            var recovery = settings.SubmissionRecovery;

            if (settings.Enabled && recovery.Enabled)
            {
                try
                {
                    await RunTickAsync(recovery, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "[Openserve][recovery] tick failed. Will retry at the next interval.");
                }
            }
            else
            {
                _logger.LogDebug("[Openserve][recovery] skipped — integration or submission recovery disabled.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(recovery.IntervalMinutes, 5, 1440)), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunTickAsync(OpenserveSubmissionRecoverySettings recovery, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOpenserveSubmissionRecoveryService>();

        var retry = await service.RunRetryPassAsync(cancellationToken);
        if (retry.HadWork)
        {
            _logger.LogInformation("[Openserve][recovery] retry pass: {Candidates} due, {Submitted} submitted, {Failed} failed, {Blocked} blocked, {Refused} not attempted, {Interrupted} interrupted claim(s) recorded.",
                retry.Candidates, retry.Submitted, retry.Failed, retry.Blocked, retry.Refused, retry.InterruptedResolved);
        }

        var sweepInterval = TimeSpan.FromHours(Math.Clamp(recovery.SafetySweepIntervalHours, 1, 24));
        var now = DateTime.UtcNow;
        if (_lastSweepUtc is null || now - _lastSweepUtc >= sweepInterval)
        {
            var sweep = await service.RunSafetySweepAsync(cancellationToken);
            _lastSweepUtc = now;
            _logger.LogInformation("[Openserve][sweep] safety sweep: {Candidates} eligible order(s) without a submission, {Submitted} submitted, {Failed} failed, {Blocked} blocked, {Refused} not attempted.",
                sweep.Candidates, sweep.Submitted, sweep.Failed, sweep.Blocked, sweep.Refused);
        }
    }
}

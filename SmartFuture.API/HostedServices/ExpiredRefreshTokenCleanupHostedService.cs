using Microsoft.EntityFrameworkCore;
using SmartFuture.Application.Persistence;

namespace SmartFuture.API.HostedServices;

/// <summary>
/// Periodically deletes refresh tokens that are both expired AND revoked-or-replaced
/// to keep the RefreshTokens table from growing unbounded. Safe to run multiple times.
/// Tolerates a missing/unavailable database during startup or transient outages.
/// </summary>
public class ExpiredRefreshTokenCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExpiredRefreshTokenCleanupHostedService> _logger;
    private readonly IConfiguration _configuration;

    public ExpiredRefreshTokenCleanupHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ExpiredRefreshTokenCleanupHostedService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue("Maintenance:ExpiredRefreshTokenCleanup:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation(
                "ExpiredRefreshTokenCleanupHostedService disabled via " +
                "Maintenance:ExpiredRefreshTokenCleanup:Enabled.");
            return;
        }

        var intervalMinutes = _configuration.GetValue<int?>(
            "Maintenance:ExpiredRefreshTokenCleanup:IntervalMinutes");

        var interval = intervalMinutes is > 0
            ? TimeSpan.FromMinutes(intervalMinutes.Value)
            : DefaultInterval;

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
                var deleted = await RunCleanupAsync(stoppingToken);
                if (deleted > 0)
                    _logger.LogInformation("Refresh-token cleanup removed {Count} expired records.", deleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Refresh-token cleanup tick failed. Will retry at the next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task<int> RunCleanupAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        var now = DateTime.UtcNow;

        return await dbContext.RefreshTokens
            .Where(rt => rt.ExpiresAtUtc < now && (rt.RevokedAtUtc != null || rt.ReplacedByToken != null))
            .ExecuteDeleteAsync(cancellationToken);
    }
}

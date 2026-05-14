using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartFuture.Infrastructure.Data;

namespace SmartFuture.API.Health;

public class AppDbContextHealthCheck : IHealthCheck
{
    private readonly AppDbContext _dbContext;

    public AppDbContextHealthCheck(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database is configured but not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connectivity check threw an exception.", ex);
        }
    }
}

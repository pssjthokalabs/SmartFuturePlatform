using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartFuture.API.Health;

namespace SmartFuture.API.Extensions;

public static class HealthCheckExtensions
{
    public const string DatabaseCheckName = "database";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddSmartFutureHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddCheck<AppDbContextHealthCheck>(
                DatabaseCheckName,
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { ReadyTag });

        return services;
    }

    public static IEndpointRouteBuilder MapSmartFutureHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteSimpleStatusAsync
        });

        endpoints.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteSimpleStatusAsync
        });

        return endpoints;
    }

    private static Task WriteSimpleStatusAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }
}

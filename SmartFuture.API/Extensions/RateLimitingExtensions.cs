using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SmartFuture.API.Extensions;

public static class RateLimitingExtensions
{
    public const string AuthPolicy = "AuthPolicy";
    public const string WebhookPolicy = "WebhookPolicy";
    public const string GeneralApiPolicy = "GeneralApiPolicy";

    public static IServiceCollection AddSmartFutureRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var authOptions = ReadOptions(configuration, "RateLimiting:Auth",
            defaultPermit: 30, defaultWindowSeconds: 60);

        var webhookOptions = ReadOptions(configuration, "RateLimiting:Webhooks",
            defaultPermit: 60, defaultWindowSeconds: 60);

        var generalOptions = ReadOptions(configuration, "RateLimiting:General",
            defaultPermit: 300, defaultWindowSeconds: 60);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(AuthPolicy, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(ctx),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = authOptions.Permit,
                        Window = TimeSpan.FromSeconds(authOptions.WindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(WebhookPolicy, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(ctx),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = webhookOptions.Permit,
                        Window = TimeSpan.FromSeconds(webhookOptions.WindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(GeneralApiPolicy, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(ctx),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = generalOptions.Permit,
                        Window = TimeSpan.FromSeconds(generalOptions.WindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));

            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    private static string PartitionKey(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static (int Permit, int WindowSeconds) ReadOptions(
        IConfiguration configuration, string section, int defaultPermit, int defaultWindowSeconds)
    {
        var permit = configuration.GetValue<int?>($"{section}:PermitLimit") ?? defaultPermit;
        var windowSeconds = configuration.GetValue<int?>($"{section}:WindowSeconds") ?? defaultWindowSeconds;

        if (permit <= 0) permit = defaultPermit;
        if (windowSeconds <= 0) windowSeconds = defaultWindowSeconds;

        return (permit, windowSeconds);
    }
}

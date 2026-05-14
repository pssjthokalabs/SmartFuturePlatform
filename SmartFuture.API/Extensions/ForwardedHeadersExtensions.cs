using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace SmartFuture.API.Extensions;

public static class ForwardedHeadersExtensions
{
    public static IServiceCollection AddSmartFutureForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            options.ForwardLimit = configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;

            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            var configuredProxies = configuration
                .GetSection("ForwardedHeaders:KnownProxies").Get<string[]>()
                ?? Array.Empty<string>();

            foreach (var proxy in configuredProxies)
            {
                if (IPAddress.TryParse(proxy, out var ip))
                    options.KnownProxies.Add(ip);
            }

            var configuredNetworks = configuration
                .GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>()
                ?? Array.Empty<string>();

            foreach (var cidr in configuredNetworks)
            {
                if (TryParseCidr(cidr, out var network))
                    options.KnownNetworks.Add(network);
            }

            // In Development, accept loopback so X-Forwarded-* from a local reverse proxy works
            // without any configuration. In any other environment, KnownProxies/KnownNetworks
            // must be configured explicitly — otherwise the headers are dropped.
            if (environment.IsDevelopment())
            {
                options.KnownProxies.Add(IPAddress.Loopback);
                options.KnownProxies.Add(IPAddress.IPv6Loopback);
            }
        });

        return services;
    }

    private static bool TryParseCidr(string cidr, out IPNetwork network)
    {
        network = default!;
        if (string.IsNullOrWhiteSpace(cidr)) return false;

        var parts = cidr.Split('/', 2);
        if (parts.Length != 2) return false;

        if (!IPAddress.TryParse(parts[0], out var ip)) return false;
        if (!int.TryParse(parts[1], out var prefix)) return false;
        if (prefix < 0) return false;

        try
        {
            network = new IPNetwork(ip, prefix);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

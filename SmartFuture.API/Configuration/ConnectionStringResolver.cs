namespace SmartFuture.API.Configuration;

/// <summary>
/// Picks the right connection-string name for the current ASPNETCORE_ENVIRONMENT.
/// Never logs the connection string value — only the selected name.
/// </summary>
internal static class ConnectionStringResolver
{
    public const string Uat = "UatConnection";
    public const string Live = "LiveConnection";

    public static (string Name, string Value) Resolve(
        IConfiguration configuration, IHostEnvironment environment)
    {
        var name = SelectName(environment);
        var value = configuration.GetConnectionString(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} is not configured for environment '{environment.EnvironmentName}'. " +
                "Set it via environment variable (e.g., ConnectionStrings__" + name + "=...), " +
                "user-secrets, or appsettings before starting the API.");
        }

        return (name, value);
    }

    public static string SelectName(IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return Uat;

        if (string.Equals(environment.EnvironmentName, "UAT", StringComparison.OrdinalIgnoreCase)
            || environment.IsStaging())
        {
            return Uat;
        }

        if (string.Equals(environment.EnvironmentName, "Live", StringComparison.OrdinalIgnoreCase)
            || environment.IsProduction())
        {
            return Live;
        }

        // Unknown environment name: fall back to Default so we don't accidentally
        // point a custom environment at Live.
        return Uat;
    }
}

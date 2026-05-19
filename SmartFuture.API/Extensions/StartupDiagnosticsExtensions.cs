using SmartFuture.API.Configuration;

namespace SmartFuture.API.Extensions;

/// <summary>
/// Prints a single non-secret diagnostic block at the very start of host construction.
/// Runs BEFORE DI registrations and BEFORE any IOptions ValidateOnStart() can throw,
/// so the output appears even when the host fails to build (e.g., missing JWT key).
///
/// Writes via Console.WriteLine: captured by AspNetCoreModuleV2 stdout when
/// stdoutLogEnabled="true" is set in web.config. Required for diagnosing IIS 500.30.
/// </summary>
public static class StartupDiagnosticsExtensions
{
    private const string JwtPlaceholder = "REPLACE_WITH_A_LONG_RANDOM_SECRET_AT_LEAST_32_CHARS";

    public static void LogStartupDiagnostics(IConfiguration configuration, IHostEnvironment environment)
    {
        var envName = string.IsNullOrWhiteSpace(environment.EnvironmentName) ? "(null)" : environment.EnvironmentName;
        var connectionName = ConnectionStringResolver.SelectName(environment);
        var connectionValue = configuration.GetConnectionString(connectionName);
        var connectionPresent = !string.IsNullOrWhiteSpace(connectionValue);

        var migrationsEnabled = configuration.GetValue<bool?>("Database:ApplyMigrationsOnStartup") ?? false;

        var swaggerEnabledSetting = configuration.GetValue<bool?>("Swagger:Enabled");
        var swaggerEnabled = swaggerEnabledSetting ?? environment.IsDevelopment();

        var jwtKey = configuration["JwtSettings:Key"] ?? string.Empty;
        var jwtKeyPresent = !string.IsNullOrWhiteSpace(jwtKey);
        var jwtKeyLength = jwtKey.Length;
        var jwtKeyIsPlaceholder = string.Equals(jwtKey, JwtPlaceholder, StringComparison.Ordinal);
        var jwtIssuer = configuration["JwtSettings:Issuer"];
        var jwtAudience = configuration["JwtSettings:Audience"];

        // PaymentSettings:MockCheckoutEnabled is the env-var gate for the
        // UAT mock-Ozow invoice/payment persistence path. Logging it on
        // startup so a missing/false setting on the wrong app pool is
        // visible without grepping ConfigurationManager — purely the bool,
        // no secrets.
        var mockCheckoutEnabled = configuration.GetValue<bool?>("PaymentSettings:MockCheckoutEnabled") ?? false;

        // Console.WriteLine flows into stdout. Captured by IIS when stdoutLogEnabled="true".
        // No secret values are printed — only presence/length/flags.
        Console.WriteLine("=== SmartFuture Startup Diagnostics ===");
        Console.WriteLine($"Environment              : {envName}");
        Console.WriteLine($"Selected connection name : {connectionName}");
        Console.WriteLine($"Connection string present: {connectionPresent}");
        Console.WriteLine($"Migrations on startup    : {migrationsEnabled}");
        Console.WriteLine($"Swagger enabled          : {swaggerEnabled}");
        Console.WriteLine($"JWT Issuer present       : {!string.IsNullOrWhiteSpace(jwtIssuer)}");
        Console.WriteLine($"JWT Audience present     : {!string.IsNullOrWhiteSpace(jwtAudience)}");
        Console.WriteLine($"JWT Key present          : {jwtKeyPresent}");
        Console.WriteLine($"JWT Key length           : {jwtKeyLength}");
        Console.WriteLine($"JWT Key is placeholder   : {jwtKeyIsPlaceholder}");
        Console.WriteLine($"PaymentSettings:MockCheckoutEnabled : {mockCheckoutEnabled}");
        Console.WriteLine("=== End diagnostics ===");
    }
}

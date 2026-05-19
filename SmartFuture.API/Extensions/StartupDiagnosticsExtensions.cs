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

        // EmailSettings:Provider drives which `INotificationSender`
        // implementation is registered. Phase 33B+ has shown that this
        // single string is the most common cause of "we set it up but
        // emails don't go out" — surface it on every boot. Only the
        // string value is logged; SMTP usernames/passwords are not.
        var emailProvider = configuration["EmailSettings:Provider"] ?? "(unset)";
        var emailProviderRecognised = string.Equals(emailProvider, "Logging", StringComparison.OrdinalIgnoreCase)
            || string.Equals(emailProvider, "Smtp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(emailProvider, "MultiSmtp", StringComparison.OrdinalIgnoreCase);

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
        Console.WriteLine($"EmailSettings:Provider              : {emailProvider}{(emailProviderRecognised ? string.Empty : " (UNRECOGNISED — falls back to LoggingNotificationSender)")}");
        WriteEmailSenderPool(configuration);
        Console.WriteLine("=== End diagnostics ===");
    }

    // Print the EmailProviders pool *without* any secret. For each
    // configured sender we show host/port/SSL/from + booleans for
    // username/password presence. If `EmailSettings:Provider` is set
    // to `MultiSmtp` but no `EmailProviders:Senders:*:Password` env
    // vars are present this block makes the gap obvious.
    private static void WriteEmailSenderPool(IConfiguration configuration)
    {
        var defaultSender = configuration["EmailProviders:DefaultSender"];
        if (!string.IsNullOrWhiteSpace(defaultSender))
        {
            Console.WriteLine($"EmailProviders:DefaultSender        : {defaultSender}");
        }

        var sendersSection = configuration.GetSection("EmailProviders:Senders");
        var senderConfigs = sendersSection.GetChildren().ToList();
        if (senderConfigs.Count == 0)
        {
            Console.WriteLine("EmailProviders:Senders              : (none configured)");
            return;
        }

        Console.WriteLine("EmailProviders:Senders              :");
        foreach (var sender in senderConfigs)
        {
            var host = sender["Host"] ?? string.Empty;
            var port = sender["Port"] ?? string.Empty;
            var enableSsl = sender["EnableSsl"] ?? string.Empty;
            var from = sender["FromEmail"] ?? string.Empty;
            var usernamePresent = !string.IsNullOrWhiteSpace(sender["Username"]);
            var passwordPresent = !string.IsNullOrWhiteSpace(sender["Password"]);

            Console.WriteLine(
                $"  - {sender.Key,-10}host={host} port={port} ssl={enableSsl} from={from} " +
                $"usernamePresent={usernamePresent} passwordPresent={passwordPresent}");
        }
    }
}

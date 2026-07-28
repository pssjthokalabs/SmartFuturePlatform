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
        Console.WriteLine($"EmailSettings:Provider              : {emailProvider} (IGNORED — see FORCED line below)");
        // Phase 35D-fix — INotificationSender is now hard-wired
        // directly to TestModeSmtpNotificationSender in AddEmailServices.
        // This banner line makes the override visible at every boot so
        // there's no confusion about which sender is active.
        Console.WriteLine("FORCED notification sender          : SmartFuture.Infrastructure.Notifications.TestModeSmtpNotificationSender");
        WriteEmailTestMode(configuration);
        WriteEmailSenderPool(configuration);
        WriteOzowConfig(configuration);
        Console.WriteLine("=== End diagnostics ===");
    }

    // Ozow callback URLs, printed verbatim at every boot.
    //
    // WHY VERBATIM: a completed Ozow payment can only become an Order if
    // Ozow's server-to-server notification reaches THIS API. When it
    // doesn't, the two candidate causes — "Ozow__NotifyUrl points at the
    // wrong host" and "the notification arrived but we rejected it" — are
    // indistinguishable without knowing the exact URL that was sent.
    // Printing it here means the answer is in the boot log before anyone
    // spends another real payment finding out.
    //
    // NotifyUrl MUST point at the API host (…/api/payments/ozow/notify),
    // NOT at the portal — the portal has no such route and would return
    // an HTML page, which Ozow treats as a delivery failure.
    // Success/Cancel/Error URLs are the opposite: they are browser
    // redirects and MUST point at the PORTAL.
    //
    // No secrets: SiteCode is masked; ApiKey/PrivateKey are presence +
    // length only.
    private static void WriteOzowConfig(IConfiguration configuration)
    {
        var enabled    = configuration.GetValue<bool?>("Ozow:Enabled") ?? false;
        var siteCode   = configuration["Ozow:SiteCode"] ?? string.Empty;
        var apiKey     = configuration["Ozow:ApiKey"] ?? string.Empty;
        var privateKey = configuration["Ozow:PrivateKey"] ?? string.Empty;
        var isTest     = configuration["Ozow:IsTest"] ?? "(unset)";
        var notifyUrl  = configuration["Ozow:NotifyUrl"] ?? string.Empty;
        var successUrl = configuration["Ozow:SuccessUrl"] ?? string.Empty;
        var cancelUrl  = configuration["Ozow:CancelUrl"] ?? string.Empty;
        var errorUrl   = configuration["Ozow:ErrorUrl"] ?? string.Empty;

        Console.WriteLine($"Ozow:Enabled                        : {enabled}");
        Console.WriteLine($"Ozow:SiteCode                       : {MaskSiteCode(siteCode)}");
        Console.WriteLine($"Ozow:ApiKey present / length        : {!string.IsNullOrWhiteSpace(apiKey)} / {apiKey.Length}");
        Console.WriteLine($"Ozow:PrivateKey present / length    : {!string.IsNullOrWhiteSpace(privateKey)} / {privateKey.Length}");
        Console.WriteLine($"Ozow:IsTest                         : {isTest}");
        Console.WriteLine($"Ozow:NotifyUrl  (MUST be API host)  : {Shown(notifyUrl)}");
        Console.WriteLine($"Ozow:SuccessUrl (portal host)       : {Shown(successUrl)}");
        Console.WriteLine($"Ozow:CancelUrl  (portal host)       : {Shown(cancelUrl)}");
        Console.WriteLine($"Ozow:ErrorUrl   (portal host)       : {Shown(errorUrl)}");
        Console.WriteLine( "Ozow notify route expected          : POST /api/payments/ozow/notify");
        Console.WriteLine( "Ozow reachability ping              : GET  /api/payments/ozow/notify/ping");

        if (!enabled) return;

        // Loud, actionable warnings for the two ways this silently breaks.
        if (string.IsNullOrWhiteSpace(notifyUrl))
        {
            Console.WriteLine("*** OZOW WARNING: NotifyUrl is EMPTY. Order-intent payments can never convert — the customer would be charged for nothing. Intent initiation refuses to run in this state.");
        }
        else if (!notifyUrl.Contains("/api/payments/ozow/notify", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"*** OZOW WARNING: NotifyUrl does not contain '/api/payments/ozow/notify' — Ozow's notification will not reach the handler. Current value: {notifyUrl}");
        }
    }

    private static string Shown(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(EMPTY)" : value;

    private static string MaskSiteCode(string? siteCode)
    {
        if (string.IsNullOrEmpty(siteCode)) return "(empty)";
        if (siteCode.Length <= 5) return new string('*', siteCode.Length);
        return $"{siteCode[..3]}***{siteCode[^2..]}";
    }

    // Phase 35D — surface EmailTestMode at every boot. When this flag
    // is on, ALL outbound mail goes through one mailbox and ignores
    // SenderType. Production app pools must keep it off; printing it
    // here makes a stray override impossible to miss.
    private static void WriteEmailTestMode(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool?>("EmailTestMode:Enabled") ?? false;
        Console.WriteLine($"EmailTestMode:Enabled               : {enabled}");
        if (!enabled) return;

        var host = configuration["EmailTestMode:Host"] ?? string.Empty;
        var port = configuration["EmailTestMode:Port"] ?? string.Empty;
        var ssl = configuration["EmailTestMode:EnableSsl"] ?? string.Empty;
        var from = configuration["EmailTestMode:FromEmail"] ?? string.Empty;
        var usernamePresent = !string.IsNullOrWhiteSpace(configuration["EmailTestMode:Username"]);
        var passwordPresent = !string.IsNullOrWhiteSpace(configuration["EmailTestMode:Password"]);

        Console.WriteLine(
            $"  EmailTestMode SMTP                : host={host} port={port} ssl={ssl} from={from} " +
            $"usernamePresent={usernamePresent} passwordPresent={passwordPresent}");
        Console.WriteLine("  (test-mode overrides EmailSettings:Provider — all mail leaves from this mailbox, SenderType is ignored)");
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

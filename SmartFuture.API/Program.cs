using SmartFuture.API.Diagnostics;
using SmartFuture.API.Extensions;

var builder = WebApplication.CreateBuilder(args);

StartupDiagnosticsExtensions.LogStartupDiagnostics(builder.Configuration, builder.Environment);

// TEMPORARY EMERGENCY DIAGNOSTIC — full UNMASKED Ozow config dump.
// Off unless Diagnostics__DumpFullOzowConfigOnStartup=true. Runs here,
// before any service registration, so it prints even if a later
// startup step throws (options ValidateOnStart, DB migrations, …) —
// a half-configured Ozow block is exactly the kind of thing that would
// otherwise kill the app before it could tell you why.
if (OzowConfigDump.IsEnabled(builder.Configuration))
{
    Console.WriteLine(OzowConfigDump.Render(
        OzowConfigDump.Build(builder.Configuration, builder.Environment)));
}

builder.Services
    .AddCoreServices()
    .AddDatabaseServices(builder.Configuration, builder.Environment)
    .AddIdentityServices(builder.Configuration)
    .AddInfrastructureServices()
    .AddCoverageServices(builder.Configuration)
    .AddAuthServices()
    .AddEmailServices(builder.Configuration)
    .AddCommunicationProviders(builder.Configuration)
    .AddApiServices()
    .AddCustomCors(builder.Configuration, builder.Environment)
    .AddSmartFutureForwardedHeaders(builder.Configuration, builder.Environment)
    .AddSmartFutureRateLimiting(builder.Configuration)
    .AddSmartFutureHealthChecks()
    .AddSmartFutureBackgroundServices();

var app = builder.Build();

await app.ApplyDatabaseMigrationsAsync();

// Prime the Openserve runtime config cache from the DB (Admin →
// Integrations → Openserve overrides) immediately at startup — without
// this, IOpenserveRuntimeConfigProvider.Current would serve
// appsettings/env-only values (a safe but stale default) until the
// reconciliation hosted service's own periodic refresh catches up,
// which could be minutes away. Never blocks/fails startup — RefreshAsync
// already swallows its own errors and logs them.
await app.Services.GetRequiredService<SmartFuture.Application.Openserve.IOpenserveRuntimeConfigProvider>()
    .RefreshAsync();

// [PaystackConfig] — single-line startup log so operators can verify
// the Paystack wiring at deploy time without spelunking config. NEVER
// logs the secret key; logs only the prefix (sk_test vs sk_live).
{
    var settings   = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmartFuture.Application.Payments.Paystack.PaystackSettings>>().Value;
    var processing = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmartFuture.Application.Payments.PaymentProcessingSettings>>().Value;
    var keyPrefix = string.IsNullOrWhiteSpace(settings.SecretKey)
        ? "(none)"
        : settings.SecretKey.StartsWith("sk_test_", StringComparison.OrdinalIgnoreCase) ? "sk_test"
        : settings.SecretKey.StartsWith("sk_live_", StringComparison.OrdinalIgnoreCase) ? "sk_live"
        : "(unknown)";
    app.Logger.LogInformation(
        "[PaystackConfig] enabled={Enabled} configured={Configured} env={Env} currency={Currency} webhookUrl={WebhookUrl} callbackUrl={CallbackUrl} secretKeyPrefix={KeyPrefix} isTestKey={IsTestKey} useTestOverride={UseOverride} testAmount={TestAmount} allowLiveOverride={AllowLiveOverride} webhookApplyEnabled={WebhookApplyEnabled}",
        settings.Enabled, settings.IsConfigured, app.Environment.EnvironmentName,
        settings.Currency, settings.WebhookUrl, settings.CallbackUrl,
        keyPrefix, settings.IsTestKey, settings.UseTestAmountOverride, settings.TestAmount,
        settings.AllowLiveTestAmountOverride, processing.WebhookApplyEnabled);
}

app.ConfigureMiddleware();

app.Run();

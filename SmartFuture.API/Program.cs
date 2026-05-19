using SmartFuture.API.Extensions;

var builder = WebApplication.CreateBuilder(args);

StartupDiagnosticsExtensions.LogStartupDiagnostics(builder.Configuration, builder.Environment);

builder.Services
    .AddCoreServices()
    .AddDatabaseServices(builder.Configuration, builder.Environment)
    .AddIdentityServices(builder.Configuration)
    .AddInfrastructureServices()
    .AddAuthServices()
    .AddEmailServices(builder.Configuration)
    .AddCommunicationProviders(builder.Configuration)
    .AddApiServices()
    .AddCustomCors(builder.Configuration)
    .AddSmartFutureForwardedHeaders(builder.Configuration, builder.Environment)
    .AddSmartFutureRateLimiting(builder.Configuration)
    .AddSmartFutureHealthChecks()
    .AddSmartFutureBackgroundServices();

var app = builder.Build();

await app.ApplyDatabaseMigrationsAsync();

app.ConfigureMiddleware();

app.Run();

using SmartFuture.API.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddCoreServices()
    .AddDatabaseServices(builder.Configuration, builder.Environment)
    .AddIdentityServices(builder.Configuration)
    .AddInfrastructureServices()
    .AddAuthServices()
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

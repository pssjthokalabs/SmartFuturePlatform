using Microsoft.EntityFrameworkCore;
using SmartFuture.API.Configuration;
using SmartFuture.Infrastructure.Data;

namespace SmartFuture.API.Extensions;

/// <summary>
/// Applies pending EF Core migrations on startup and invokes the existing seed routine.
/// Connection string values are never logged — only the resolved name + environment.
/// </summary>
public static class DatabaseMigrationExtensions
{
    public static async Task ApplyDatabaseMigrationsAsync(this WebApplication app)
    {
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("SmartFuture.Startup.Migrations");

        var enabled = app.Configuration.GetValue<bool?>("Database:ApplyMigrationsOnStartup") ?? false;
        var connectionName = ConnectionStringResolver.SelectName(app.Environment);

        if (!enabled)
        {
            logger.LogInformation(
                "Startup migrations are disabled for environment '{Environment}' (connection '{Connection}'). " +
                "Set Database:ApplyMigrationsOnStartup=true (or env var Database__ApplyMigrationsOnStartup=true) to enable.",
                app.Environment.EnvironmentName, connectionName);
            return;
        }

        logger.LogInformation(
            "Applying pending EF Core migrations for environment '{Environment}' (connection '{Connection}').",
            app.Environment.EnvironmentName, connectionName);

        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            await dbContext.Database.MigrateAsync();
            logger.LogInformation("EF Core migrations applied successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Startup migration failed for environment '{Environment}' (connection '{Connection}'). " +
                "Application will not start.",
                app.Environment.EnvironmentName, connectionName);
            throw;
        }

        // Existing seeding (roles only — no fake/demo data). SeedDatabaseAsync internally
        // swallows + logs; if you want hard-fail on seed failure, change its catch to rethrow.
        await app.Services.SeedDatabaseAsync();
    }
}

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

        // Always report the pending-migration list regardless of the
        // apply flag — if the operator turned auto-apply off, this
        // tells them up front what's expected vs. what's actually in
        // the database, so 500s from missing columns aren't a mystery.
        // Done in its own scope + try/catch so a transient connection
        // hiccup at startup doesn't take down the API.
        try
        {
            using var diagScope = app.Services.CreateScope();
            var diagDbContext = diagScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = (await diagDbContext.Database.GetPendingMigrationsAsync()).ToArray();
            if (pending.Length == 0)
            {
                logger.LogInformation(
                    "EF Core schema is up to date for environment '{Environment}' (connection '{Connection}'). No pending migrations.",
                    app.Environment.EnvironmentName, connectionName);
            }
            else
            {
                logger.LogWarning(
                    "EF Core has {Count} pending migration(s) for environment '{Environment}' (connection '{Connection}'): {Migrations}. " +
                    "Endpoints that touch new columns/tables will return 500 until these are applied.",
                    pending.Length, app.Environment.EnvironmentName, connectionName, string.Join(", ", pending));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not enumerate pending EF Core migrations for environment '{Environment}' (connection '{Connection}'). " +
                "Continuing startup; the application may still start but schema-dependent endpoints may fail.",
                app.Environment.EnvironmentName, connectionName);
        }

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

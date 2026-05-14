using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartFuture.API.Configuration;
using SmartFuture.Infrastructure.Data;

namespace SmartFuture.API.Extensions;

/// <summary>
/// Applies pending EF Core migrations on startup and invokes the existing seed routine.
/// Concurrency-safe across multiple app instances via SQL Server sp_getapplock.
/// Connection string values are never logged — only the resolved name + environment.
/// </summary>
public static class DatabaseMigrationExtensions
{
    private const string AppLockResource = "SmartFuture_EF_Migrations";
    private const int AppLockTimeoutMilliseconds = 60_000;
    private const string LockOwner = "Session";

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
            // Hold a single connection open for the duration so the session-scoped
            // sp_getapplock stays valid across MigrateAsync's internal transactions.
            await dbContext.Database.OpenConnectionAsync();

            try
            {
                var lockResult = await AcquireAppLockAsync(dbContext);
                if (lockResult < 0)
                {
                    throw new InvalidOperationException(
                        $"Could not acquire migration lock '{AppLockResource}'. " +
                        $"sp_getapplock returned {lockResult} ({DescribeLockResult(lockResult)}). " +
                        "Aborting startup to avoid concurrent schema changes.");
                }

                logger.LogInformation(
                    "Migration lock '{LockResource}' acquired (result={LockResult}). Running MigrateAsync.",
                    AppLockResource, lockResult);

                try
                {
                    await dbContext.Database.MigrateAsync();
                    logger.LogInformation("EF Core migrations applied successfully.");
                }
                finally
                {
                    await TryReleaseAppLockAsync(dbContext, logger);
                }
            }
            finally
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Startup migration failed for environment '{Environment}' (connection '{Connection}'). " +
                "Application will not start.",
                app.Environment.EnvironmentName, connectionName);
            throw;
        }

        // Run existing seeding (roles only — no fake/demo data).
        // SeedDatabaseAsync internally swallows + logs; if you want hard-fail on
        // seed failure, change SeedDatabaseAsync's catch to rethrow.
        await app.Services.SeedDatabaseAsync();
    }

    private static async Task<int> AcquireAppLockAsync(AppDbContext dbContext)
    {
        var resourceParam = new SqlParameter("@Resource", AppLockResource);
        var lockModeParam = new SqlParameter("@LockMode", "Exclusive");
        var lockOwnerParam = new SqlParameter("@LockOwner", LockOwner);
        var lockTimeoutParam = new SqlParameter("@LockTimeout", AppLockTimeoutMilliseconds);
        var resultParam = new SqlParameter
        {
            ParameterName = "@result",
            SqlDbType = SqlDbType.Int,
            Direction = ParameterDirection.Output
        };

        await dbContext.Database.ExecuteSqlRawAsync(
            "EXEC @result = sp_getapplock @Resource = @Resource, @LockMode = @LockMode, " +
            "@LockOwner = @LockOwner, @LockTimeout = @LockTimeout",
            resourceParam, lockModeParam, lockOwnerParam, lockTimeoutParam, resultParam);

        return resultParam.Value is int v ? v : -999;
    }

    private static async Task TryReleaseAppLockAsync(AppDbContext dbContext, ILogger logger)
    {
        try
        {
            var resourceParam = new SqlParameter("@Resource", AppLockResource);
            var lockOwnerParam = new SqlParameter("@LockOwner", LockOwner);

            await dbContext.Database.ExecuteSqlRawAsync(
                "EXEC sp_releaseapplock @Resource = @Resource, @LockOwner = @LockOwner",
                resourceParam, lockOwnerParam);
        }
        catch (Exception ex)
        {
            // sp_releaseapplock failure is non-fatal because closing the connection
            // releases a Session-scoped lock automatically.
            logger.LogWarning(ex,
                "Failed to explicitly release migration lock '{LockResource}'. " +
                "Connection close will release it.", AppLockResource);
        }
    }

    private static string DescribeLockResult(int result) => result switch
    {
        0 => "lock granted",
        1 => "lock granted after waiting",
        -1 => "lock request timed out",
        -2 => "lock request cancelled",
        -3 => "lock request chosen as deadlock victim",
        -999 => "parameter / call error",
        _ => "unknown sp_getapplock return code"
    };
}

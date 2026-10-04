using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFuture.Application.Common.Security;

namespace SmartFuture.Infrastructure.Security;

/// <summary>
/// The app's single ASP.NET Core DataProtection registration, with a
/// DURABLE key ring.
///
/// WHY THIS EXISTS (root cause of "saved Openserve API key later shows Not
/// set"): DataProtection was registered with only SetApplicationName and no
/// key repository. On an IIS app pool without a loaded user profile —
/// standard on shared Windows hosting such as the Site4Now UAT/LIVE sites —
/// DataProtection falls back to an IN-MEMORY key ring that is thrown away
/// whenever the process stops (idle-timeout shutdown, recycle, deploy). The
/// encrypted API key stayed in the database, but after the next process
/// start the key that encrypted it no longer existed, Unprotect threw, the
/// runtime config treated the secret as unset and the admin console showed
/// "Not configured". The same failure applies to stored Paystack/PayFast
/// mandate tokens.
///
/// Fix: persist the key ring to a directory (default
/// {ContentRoot}/App_Data/DataProtection-Keys, override with
/// DataProtection:KeysPath). App_Data is under the content root, never under
/// wwwroot, so it is not web-served; MSDeploy publishes here run with
/// SkipExtraFilesOnServer, so existing key files survive deploys. Key files
/// can optionally be DPAPI-encrypted at rest (DataProtection:ProtectKeysWithDpapi=true,
/// LocalMachine scope — machine-bound, so keep it off if the host may move
/// the site between servers). If the directory isn't writable, startup does
/// not fail: the default store is kept and the problem is surfaced in the
/// startup log and in Admin → Integrations → Openserve → Readiness.
/// </summary>
public static class SmartFutureDataProtection
{
    /// <summary>Unchanged from the previous registration — the protector purpose chain depends on it.</summary>
    public const string ApplicationName = "SmartFuture.API";

    public const string KeysPathConfigKey = "DataProtection:KeysPath";
    public const string ProtectKeysWithDpapiConfigKey = "DataProtection:ProtectKeysWithDpapi";

    public static readonly string DefaultRelativeKeysPath = Path.Combine("App_Data", "DataProtection-Keys");

    public static DataProtectionKeyRingStatus AddSmartFutureDataProtection(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        var keysPath = ResolveKeysPath(configuration[KeysPathConfigKey], contentRootPath);

        DataProtectionKeyRingStatus status;
        if (TryEnsureWritableDirectory(keysPath, out var problem))
        {
            var seeded = SeedFromDefaultProfileStore(keysPath, DefaultProfileKeyStoreDirectory());
            builder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

            var protectWithDpapi = false;
            if (configuration.GetValue<bool>(ProtectKeysWithDpapiConfigKey) && OperatingSystem.IsWindows())
            {
                builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
                protectWithDpapi = true;
            }

            status = new DataProtectionKeyRingStatus
            {
                IsPersistent = true,
                KeysDirectory = keysPath,
                KeysProtectedAtRest = protectWithDpapi,
                SeededKeyCount = seeded,
                Description = protectWithDpapi
                    ? "Persisted to disk, key files DPAPI-encrypted (LocalMachine)."
                    : "Persisted to disk (key files readable by the site's own account only)."
            };
        }
        else
        {
            status = new DataProtectionKeyRingStatus
            {
                IsPersistent = false,
                KeysDirectory = keysPath,
                Description = "NOT persisted — using the host's default key store, which on IIS without a user profile is in-memory only.",
                Problem = problem
            };
        }

        services.AddSingleton(status);
        return status;
    }

    public static string ResolveKeysPath(string? configuredPath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return Path.GetFullPath(Path.Combine(contentRootPath, DefaultRelativeKeysPath));
        var trimmed = configuredPath.Trim();
        return Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(contentRootPath, trimmed));
    }

    /// <summary>Creates the directory and proves the process can write and delete a file in it.</summary>
    public static bool TryEnsureWritableDirectory(string path, out string? problem)
    {
        problem = null;
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            problem = $"Cannot write DataProtection keys to '{path}': {ex.GetType().Name}: {ex.Message}. Grant the site's application pool write access to this folder, or set {KeysPathConfigKey} to a writable folder.";
            return false;
        }
    }

    /// <summary>
    /// One-time seed: when the new directory has no keys yet but the host
    /// previously kept a durable key ring in the app pool's user profile
    /// (the DataProtection default when a profile IS loaded), copy those key
    /// files across so secrets they encrypted stay readable after the switch.
    /// Never overwrites; returns the number of files copied.
    /// </summary>
    public static int SeedFromDefaultProfileStore(string keysPath, string? profileStorePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileStorePath) || !Directory.Exists(profileStorePath)) return 0;
            if (string.Equals(Path.GetFullPath(profileStorePath).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(keysPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return 0;
            if (Directory.EnumerateFiles(keysPath, "key-*.xml").Any()) return 0;

            var copied = 0;
            foreach (var source in Directory.EnumerateFiles(profileStorePath, "key-*.xml"))
            {
                var destination = Path.Combine(keysPath, Path.GetFileName(source));
                if (File.Exists(destination)) continue;
                File.Copy(source, destination);
                copied++;
            }
            return copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Where DataProtection's default repository keeps keys when a user profile is available.</summary>
    private static string? DefaultProfileKeyStoreDirectory()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "ASP.NET", "DataProtection-Keys");
    }
}

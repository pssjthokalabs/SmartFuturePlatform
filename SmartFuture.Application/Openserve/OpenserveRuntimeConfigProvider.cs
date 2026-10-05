using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Singleton. Keeps a merged settings snapshot in memory so every
/// caller — including the inbound-callback hot path — gets a
/// synchronous read. See <see cref="IOpenserveRuntimeConfigProvider"/>
/// for the merge contract.
/// </summary>
public class OpenserveRuntimeConfigProvider : IOpenserveRuntimeConfigProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<OpenserveFulfilmentSettings> _fallback;
    private readonly IOpenserveSecretProtector _protector;
    private readonly ILogger<OpenserveRuntimeConfigProvider> _logger;

    private OpenserveFulfilmentSettings _current;
    private OpenserveSecretState _secretState = OpenserveSecretState.NotLoaded;

    public OpenserveRuntimeConfigProvider(
        IServiceScopeFactory scopeFactory, IOptionsMonitor<OpenserveFulfilmentSettings> fallback,
        IOpenserveSecretProtector protector, ILogger<OpenserveRuntimeConfigProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _fallback = fallback;
        _protector = protector;
        _logger = logger;
        // Safe value from the moment the app boots, before the first
        // RefreshAsync has had a chance to run — identical behaviour to
        // before this provider existed.
        _current = fallback.CurrentValue;
    }

    public OpenserveFulfilmentSettings Current => Volatile.Read(ref _current);

    public OpenserveSecretState SecretState => Volatile.Read(ref _secretState);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var row = await db.OpenserveIntegrationConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == OpenserveIntegrationConfig.SingletonId, cancellationToken);

            var (merged, secretState) = Merge(_fallback.CurrentValue, row);
            Volatile.Write(ref _current, merged);
            Volatile.Write(ref _secretState, secretState);
        }
        catch (Exception ex)
        {
            // A transient DB hiccup (or the migration not applied yet on
            // an environment mid-rollout) must never break Current for
            // every other caller — keep serving the last-known-good
            // snapshot and log loudly so it's visible in the Overview
            // health card.
            _logger.LogError(ex, "[Openserve][config] Failed to refresh runtime configuration from the database; keeping the previous cached snapshot.");
        }
    }

    private (OpenserveFulfilmentSettings Settings, OpenserveSecretState SecretState) Merge(OpenserveFulfilmentSettings fallback, OpenserveIntegrationConfig? row)
    {
        if (row is null) return (fallback, new OpenserveSecretState { LoadedAtUtc = DateTime.UtcNow });

        // A stored secret that no longer decrypts is NOT the same as "not
        // set": the ciphertext is still in the DB, but the DataProtection
        // key that encrypted it is gone (non-persistent key ring). Record
        // that explicitly so the admin console can say so instead of
        // silently reporting "Not configured".
        var apiKey = TryUnprotect(row.ApiKeyProtected, _protector.UnprotectApiKey, "ApiKey");
        var sharedSecret = TryUnprotect(row.SharedSecretProtected, _protector.UnprotectSharedSecret, "SharedSecret");
        var secretState = new OpenserveSecretState
        {
            LoadedAtUtc = DateTime.UtcNow,
            ApiKeyStoredInDatabase = !string.IsNullOrEmpty(row.ApiKeyProtected),
            ApiKeyUnreadable = !string.IsNullOrEmpty(row.ApiKeyProtected) && apiKey is null,
            SharedSecretStoredInDatabase = !string.IsNullOrEmpty(row.SharedSecretProtected),
            SharedSecretUnreadable = !string.IsNullOrEmpty(row.SharedSecretProtected) && sharedSecret is null
        };

        var settings = new OpenserveFulfilmentSettings
        {
            Enabled = row.Enabled ?? fallback.Enabled,
            BaseUrl = Coalesce(row.BaseUrl, fallback.BaseUrl),
            WsIspCode = Coalesce(row.WsIspCode, fallback.WsIspCode),
            IspIdentifier = Coalesce(row.IspIdentifier, fallback.IspIdentifier),
            SenderId = Coalesce(row.SenderId, fallback.SenderId),
            ReplyToAddress = Coalesce(row.ReplyToAddress, fallback.ReplyToAddress),
            EventNotificationUrl = Coalesce(row.EventNotificationUrl, fallback.EventNotificationUrl),
            HttpTimeoutSeconds = row.HttpTimeoutSeconds ?? fallback.HttpTimeoutSeconds,
            PollingFallbackIntervalMinutes = row.PollingFallbackIntervalMinutes ?? fallback.PollingFallbackIntervalMinutes,
            // Not exposed for DB override (see brief §2 — flagged as
            // unwired/config-only); always comes from appsettings.
            Retry = fallback.Retry,
            SubmissionRecovery = fallback.SubmissionRecovery,
            Qualification = fallback.Qualification,
            ApiKey = apiKey ?? fallback.ApiKey,
            CallbackAuth = new OpenserveCallbackAuthSettings
            {
                Mode = row.CallbackAuthMode ?? fallback.CallbackAuth.Mode,
                SharedSecret = sharedSecret ?? fallback.CallbackAuth.SharedSecret,
                AllowedIpRanges = !string.IsNullOrWhiteSpace(row.AllowedIpRangesCsv)
                    ? row.AllowedIpRangesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : fallback.CallbackAuth.AllowedIpRanges
            }
        };
        return (settings, secretState);
    }

    // A DB override cleared back to empty string behaves the same as
    // "never overridden" — falls back to appsettings/env, rather than
    // becoming a stored blank value. Matches ordinary "clear this field"
    // web-form UX.
    private static string Coalesce(string? overrideValue, string fallbackValue)
        => !string.IsNullOrWhiteSpace(overrideValue) ? overrideValue : fallbackValue;

    private string? TryUnprotect(string? blob, Func<string, string> unprotect, string secretName)
    {
        if (string.IsNullOrEmpty(blob)) return null;
        try
        {
            return unprotect(blob);
        }
        catch (Exception ex)
        {
            // The key that encrypted this secret is no longer in the
            // DataProtection key ring (it was not persisted across a process
            // restart, or the ring was replaced). Never crash every caller of
            // Current — but say exactly what happened. Neither the
            // ciphertext nor any plaintext is logged.
            _logger.LogError("[Openserve][config] Stored Openserve {SecretName} cannot be decrypted ({ErrorType}: {ErrorMessage}). The DataProtection key that encrypted it is no longer available — re-enter it in Admin → Integrations → Openserve.",
                secretName, ex.GetType().Name, ex.Message);
            return null;
        }
    }
}

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

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var row = await db.OpenserveIntegrationConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == OpenserveIntegrationConfig.SingletonId, cancellationToken);

            var merged = Merge(_fallback.CurrentValue, row);
            Volatile.Write(ref _current, merged);
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

    private OpenserveFulfilmentSettings Merge(OpenserveFulfilmentSettings fallback, OpenserveIntegrationConfig? row)
    {
        if (row is null) return fallback;

        return new OpenserveFulfilmentSettings
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
            ApiKey = TryUnprotect(row.ApiKeyProtected, _protector.UnprotectApiKey) ?? fallback.ApiKey,
            CallbackAuth = new OpenserveCallbackAuthSettings
            {
                Mode = row.CallbackAuthMode ?? fallback.CallbackAuth.Mode,
                SharedSecret = TryUnprotect(row.SharedSecretProtected, _protector.UnprotectSharedSecret) ?? fallback.CallbackAuth.SharedSecret,
                AllowedIpRanges = !string.IsNullOrWhiteSpace(row.AllowedIpRangesCsv)
                    ? row.AllowedIpRangesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : fallback.CallbackAuth.AllowedIpRanges
            }
        };
    }

    // A DB override cleared back to empty string behaves the same as
    // "never overridden" — falls back to appsettings/env, rather than
    // becoming a stored blank value. Matches ordinary "clear this field"
    // web-form UX.
    private static string Coalesce(string? overrideValue, string fallbackValue)
        => !string.IsNullOrWhiteSpace(overrideValue) ? overrideValue : fallbackValue;

    private string? TryUnprotect(string? blob, Func<string, string> unprotect)
    {
        if (string.IsNullOrEmpty(blob)) return null;
        try
        {
            return unprotect(blob);
        }
        catch (Exception ex)
        {
            // DataProtection key ring rotated/unavailable — treat as
            // "not set" rather than crash every caller of Current.
            _logger.LogError(ex, "[Openserve][config] Failed to unprotect a stored secret; treating it as unset.");
            return null;
        }
    }
}

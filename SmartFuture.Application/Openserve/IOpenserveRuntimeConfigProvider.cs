namespace SmartFuture.Application.Openserve;

/// <summary>
/// The single source of truth every Openserve consumer reads settings
/// from — replaces direct <c>IOptionsMonitor&lt;OpenserveFulfilmentSettings&gt;</c>
/// usage everywhere except inside this provider itself.
///
/// Merges, per field, a DB-persisted admin override
/// (<c>OpenserveIntegrationConfig</c>, edited via Admin → Integrations →
/// Openserve) on top of the IConfiguration-bound defaults (appsettings/
/// environment variables), which remain the bootstrap/fallback layer:
/// a field left unset in the DB (null) falls back to config; once the
/// DB row exists at all, any field an admin DID set there wins.
///
/// <see cref="Current"/> is a synchronous, cached snapshot — safe to
/// read on hot paths (e.g. inbound-callback auth checking on every
/// request) without a DB round trip. <see cref="RefreshAsync"/> re-reads
/// the DB and updates the cache; call it after every admin write and
/// once at application startup.
/// </summary>
public interface IOpenserveRuntimeConfigProvider
{
    OpenserveFulfilmentSettings Current { get; }

    /// <summary>Whether the DB-stored secrets behind <see cref="Current"/> were present and decryptable at the last refresh. May be null on test doubles — treat null as "unknown".</summary>
    OpenserveSecretState? SecretState => null;

    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Stored-secret health as of the last <see cref="IOpenserveRuntimeConfigProvider.RefreshAsync"/>.
/// "Stored but unreadable" means the encrypted value is still in the
/// database but the DataProtection key that encrypted it is gone — the
/// exact failure behind "API key later shows Not set".
/// </summary>
public sealed record OpenserveSecretState
{
    public static OpenserveSecretState NotLoaded { get; } = new();

    public DateTime? LoadedAtUtc { get; init; }
    public bool ApiKeyStoredInDatabase { get; init; }
    public bool ApiKeyUnreadable { get; init; }
    public bool SharedSecretStoredInDatabase { get; init; }
    public bool SharedSecretUnreadable { get; init; }
}

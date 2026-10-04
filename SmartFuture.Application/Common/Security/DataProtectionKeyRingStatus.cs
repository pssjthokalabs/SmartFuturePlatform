namespace SmartFuture.Application.Common.Security;

/// <summary>
/// How the app's ASP.NET Core DataProtection key ring is stored, decided
/// once at startup (see SmartFutureDataProtection in Infrastructure).
///
/// Every secret the app encrypts at rest — the Openserve API key and
/// callback shared secret (Admin → Integrations → Openserve) and the
/// Paystack / PayFast recurring-payment mandate tokens — is only readable
/// for as long as the key that encrypted it survives. A non-persistent key
/// ring (the default on IIS app pools without a loaded user profile, i.e.
/// typical shared hosting) is regenerated on every process start, which
/// silently turns every stored secret into undecryptable bytes.
/// </summary>
public sealed class DataProtectionKeyRingStatus
{
    /// <summary>True when keys are written to a durable directory that survives recycles, restarts and deploys.</summary>
    public bool IsPersistent { get; init; }

    /// <summary>Directory holding the key XML files, when persistent.</summary>
    public string? KeysDirectory { get; init; }

    /// <summary>True when the key files themselves are encrypted at rest (Windows DPAPI).</summary>
    public bool KeysProtectedAtRest { get; init; }

    /// <summary>Number of pre-existing key files copied in from the default user-profile key store on first use, so secrets encrypted before the switch stay readable.</summary>
    public int SeededKeyCount { get; init; }

    /// <summary>One-line, admin-readable description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Why persistence could not be enabled, when it couldn't.</summary>
    public string? Problem { get; init; }

    /// <summary>Used where no startup decision was registered (e.g. unit tests).</summary>
    public static DataProtectionKeyRingStatus Unknown { get; } = new()
    {
        IsPersistent = false,
        Description = "Key ring status unknown — DataProtection persistence was not configured by this host.",
        Problem = "Not configured."
    };
}

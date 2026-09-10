using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Domain.Openserve;

/// <summary>
/// Single-row runtime override for Openserve fulfilment configuration —
/// same "singleton settings row" pattern as JobModuleSettings. Every
/// field is nullable: null means "not overridden here, fall back to
/// appsettings/environment variables" (see IOpenserveRuntimeConfigProvider,
/// the only thing that reads this table). Admin edits flow through the
/// Integrations → Openserve console rather than a server file edit.
///
/// Secrets (ApiKey, CallbackAuth shared secret) are NEVER stored in
/// plaintext — only as ASP.NET Core DataProtection ciphertext via
/// IOpenserveSecretProtector, the same mechanism already used for
/// CustomerPaymentMandate.AuthorizationCode (see DataProtectionMandateProtector).
/// </summary>
public class OpenserveIntegrationConfig : BaseEntity
{
    // Fixed id for the one and only settings row.
    public static readonly Guid SingletonId = new("66d6a6b1-1a0c-4e6a-9c0e-0be0a1f7c001");

    public bool? Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public string? WsIspCode { get; set; }
    public string? IspIdentifier { get; set; }
    public string? SenderId { get; set; }
    public string? ReplyToAddress { get; set; }
    public string? EventNotificationUrl { get; set; }
    public int? HttpTimeoutSeconds { get; set; }
    public int? PollingFallbackIntervalMinutes { get; set; }
    public OpenserveCallbackAuthMode? CallbackAuthMode { get; set; }

    /// <summary>Comma-separated CIDR/literal IPs — only meaningful when CallbackAuthMode = IpAllowlist.</summary>
    public string? AllowedIpRangesCsv { get; set; }

    /// <summary>DataProtection ciphertext of the Openserve api_key. Never the plaintext.</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>DataProtection ciphertext of the callback shared secret. Never the plaintext.</summary>
    public string? SharedSecretProtected { get; set; }

    public Guid? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
}

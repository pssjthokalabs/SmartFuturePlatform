namespace SmartFuture.Shared.Enums.Openserve;

/// <summary>
/// The Fulfilment API Spec does NOT document any signature/HMAC/shared-
/// secret scheme for inbound callbacks or event notifications. Defaults
/// to <see cref="None"/> so the endpoint is wired but the trust
/// decision stays explicit and reviewable rather than silently
/// accepting unauthenticated traffic in production.
/// </summary>
public enum OpenserveCallbackAuthMode
{
    /// <summary>No authentication beyond the URL itself being unguessable. Not safe for production until Openserve confirms a real mechanism.</summary>
    None = 0,
    SharedSecretHeader = 1,
    IpAllowlist = 2
}

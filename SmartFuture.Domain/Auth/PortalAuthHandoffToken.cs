using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Auth;

namespace SmartFuture.Domain.Auth;

// One-time, short-lived token used to sign a freshly-registered website
// visitor into the portal without re-typing their password. The raw
// token is returned to the website **once** and never persisted; only
// `TokenHash` (SHA-256) is stored. Consuming the token issues a normal
// `AuthTokenDto` (access + refresh) — exactly what /api/auth/login
// would have returned — and marks this row consumed so the token
// cannot be replayed.
//
// Lifetime is intentionally tight (~10 minutes) because the website
// uses the token immediately after creating it.
public class PortalAuthHandoffToken : BaseEntity
{
    public string TokenHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public User? User { get; set; }

    // Optional intent token to pass through the handoff for the portal
    // to land on `/client/orders/new?intentToken=…` afterwards. Kept on
    // the row so the portal can use it without trusting query strings.
    public string? IntentToken { get; set; }

    public PortalAuthHandoffPurpose Purpose { get; set; } = PortalAuthHandoffPurpose.WebsiteRegistration;

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }

    public string? CreatedIpAddress { get; set; }
    public string? CreatedUserAgent { get; set; }

    public string? ConsumedIpAddress { get; set; }
    public string? ConsumedUserAgent { get; set; }
}

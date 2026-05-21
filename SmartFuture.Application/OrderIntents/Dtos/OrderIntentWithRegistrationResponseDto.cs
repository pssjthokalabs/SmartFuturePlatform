namespace SmartFuture.Application.OrderIntents.Dtos;

// What the website needs after a successful POST /api/public/
// order-intents/register: a portal-handoff URL it can simply assign to
// `window.location.href` and forget. The intent + handoff tokens are
// also returned individually for clients that want to build the URL
// themselves.
public class OrderIntentWithRegistrationResponseDto
{
    public string IntentToken { get; set; } = string.Empty;

    /// <summary>Raw one-time handoff token. Exposed once; never persisted client-side.</summary>
    public string HandoffToken { get; set; } = string.Empty;

    public DateTime IntentExpiresAtUtc { get; set; }
    public DateTime HandoffExpiresAtUtc { get; set; }

    /// <summary>
    /// Path the portal listens on. e.g.
    /// `/client/auth/handoff?token=&lt;handoffToken&gt;&intentToken=&lt;intentToken&gt;`.
    /// The website joins it onto its configured portal URL.
    /// </summary>
    public string PortalHandoffPath { get; set; } = string.Empty;
}

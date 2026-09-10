using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Openserve Product Ordering / Fulfilment API runtime configuration.
/// Bound from the <c>"OpenserveFulfilment"</c> configuration section
/// (env-var prefix <c>OpenserveFulfilment__</c>). Deliberately named
/// differently from the existing <c>CoverageSettings:Openserve</c>
/// section, which configures the separate, unauthenticated public GIS
/// coverage-check endpoint used by <c>OpenserveFibreCoverageProvider</c>
/// — this section is for the authenticated reseller order/fulfilment
/// API described in the Openserve Fulfilment API Specification
/// (ITSD-179559 Rev 04.002).
///
/// Secrets (<see cref="ApiKey"/>, <see cref="OpenserveCallbackAuthSettings.SharedSecret"/>)
/// MUST come from env vars / user-secrets — the committed
/// appsettings.json carries empty placeholders only.
///
/// <see cref="Enabled"/> defaults to false. Until Openserve confirms
/// the open items tracked in OPENSERVE_INTEGRATION_RESEARCH.md
/// (production BaseUrl, callback authentication scheme, and whether
/// <see cref="WsIspCode"/> and/or <see cref="IspIdentifier"/> is the
/// field the client's "Smartfuture 04" reference maps to), this stays
/// disabled — order submission, callbacks and the reconciliation
/// worker all no-op while Enabled=false, mirroring the existing
/// Provisioning:Enabled / Paystack:Enabled kill-switch pattern.
/// </summary>
public class OpenserveFulfilmentSettings
{
    public const string SectionName = "OpenserveFulfilment";

    /// <summary>Master kill switch. Production default: false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// API base URL, e.g. "https://testapitrx.openserve.co.za" for
    /// Openserve's shared test host. Production URL is "TBC as part of
    /// the onboarding process" per the spec — not something to guess.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// **Secret.** The <c>api_key</c> header value shared by Openserve's
    /// ESB team during onboarding. Never logged, never returned to any
    /// frontend.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The <c>{ws-ispcode}</c> URL path segment issued by the Openserve
    /// Commercial team during onboarding (spec §1.7). NOT confirmed to
    /// be the same string as <see cref="IspIdentifier"/> — kept as an
    /// independent setting until the client confirms which of these
    /// (possibly both) "Smartfuture 04" maps to.
    /// </summary>
    public string WsIspCode { get; set; } = string.Empty;

    /// <summary>
    /// The "ISP Identifier" serviceCharacteristic value sent inside
    /// every order payload (spec §4.1.2.12). Sample values in the spec
    /// follow a "WS &lt;NAME&gt;" pattern (e.g. "WS TEST", "WS MTN").
    /// See <see cref="WsIspCode"/> remarks — independently configurable
    /// on purpose.
    /// </summary>
    public string IspIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// Mandatory "ReplyToAddress" header value sent on every order
    /// call — the callback URL Openserve posts the async order result
    /// to. Must be a publicly reachable HTTPS endpoint on our API.
    /// </summary>
    public string ReplyToAddress { get; set; } = string.Empty;

    /// <summary>
    /// The event-notification endpoint URL registered with Openserve
    /// during onboarding (separate registration process from
    /// ReplyToAddress per spec §1.7 — informational here; Openserve
    /// configures it on their side, this is just what we tell them to
    /// point at).
    /// </summary>
    public string EventNotificationUrl { get; set; } = string.Empty;

    /// <summary>Optional "SenderID" header, e.g. "Connect App". Openserve's own samples use values like "B2B", "UPP", "WhatsApp".</summary>
    public string SenderId { get; set; } = "SmartFuture";

    /// <summary>HTTP client timeout for outbound Openserve calls.</summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    public OpenserveRetrySettings Retry { get; set; } = new();

    /// <summary>
    /// Reconciliation-worker poll interval for non-terminal orders
    /// (brief §15). Webhooks are the primary mechanism — this is the
    /// safety-net fallback, so keep it conservative.
    /// </summary>
    public int PollingFallbackIntervalMinutes { get; set; } = 30;

    public OpenserveCallbackAuthSettings CallbackAuth { get; set; } = new();

    /// <summary>True once the minimum fields needed to actually call Openserve are present.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(WsIspCode)
        && !string.IsNullOrWhiteSpace(ReplyToAddress);
}

public class OpenserveRetrySettings
{
    /// <summary>Max transport-level retry attempts for a single business submission (network/5xx only — never retries a business rejection).</summary>
    public int MaxAttempts { get; set; } = 3;

    public int BaseDelaySeconds { get; set; } = 5;
}

/// <summary>
/// Openserve's spec does not document any signature/HMAC/shared-secret
/// scheme for inbound callbacks or event notifications (flagged as an
/// open question). <see cref="Mode"/> defaults to
/// <see cref="OpenserveCallbackAuthMode.None"/> so the endpoint is
/// wired but the trust decision stays explicit and reviewable rather
/// than silently accepting unauthenticated traffic in production —
/// see the callback controller's own gating for how this is enforced.
/// </summary>
public class OpenserveCallbackAuthSettings
{
    public OpenserveCallbackAuthMode Mode { get; set; } = OpenserveCallbackAuthMode.None;

    /// <summary>**Secret.** Used only when Mode requires a shared secret.</summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>Optional IP allowlist (CIDR or literal), used only when Mode includes IP checking.</summary>
    public string[] AllowedIpRanges { get; set; } = Array.Empty<string>();
}

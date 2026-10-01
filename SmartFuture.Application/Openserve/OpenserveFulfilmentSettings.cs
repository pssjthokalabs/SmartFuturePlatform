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
/// Openserve terminology (Postman collection variables) → this class:
///   HOST_URL       → <see cref="BaseUrl"/> (stored with https:// scheme)
///   API_KEY        → <see cref="ApiKey"/> (secret)
///   isp_tag        → <see cref="WsIspCode"/>      e.g. "ws-marut"
///   ISPID          → <see cref="IspIdentifier"/>  e.g. "WS MARUT"
///   SenderID       → <see cref="SenderId"/>       e.g. "SMARTFUTURE"
///   ReplyToAddress → <see cref="ReplyToAddress"/> (Openserve-provided URL)
/// isp_tag and ISPID are different values with different casing and are
/// used in different places — never derive one from the other.
///
/// <see cref="Enabled"/> defaults to false — order submission, callbacks
/// and the reconciliation worker all no-op while Enabled=false, mirroring
/// the existing Provisioning:Enabled / Paystack:Enabled kill-switch pattern.
/// </summary>
public class OpenserveFulfilmentSettings
{
    public const string SectionName = "OpenserveFulfilment";

    /// <summary>Master kill switch. Production default: false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Openserve HOST_URL with scheme, e.g. "https://stapitrx.openserve.co.za"
    /// for Smart Future's provisioned staging/UAT tenant. Production host is
    /// "TBC as part of the onboarding process" per the spec — not something
    /// to guess.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// **Secret.** Openserve API_KEY — the <c>api_key</c> header value issued
    /// per environment. Never logged, never persisted in plaintext, never
    /// returned to any frontend (admin sees a masked tail only).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Openserve <c>isp_tag</c> (the PDF's "{ws-ispcode}"), e.g. "ws-marut".
    /// Used as the URL path segment on EVERY call, and as the FromLocation
    /// header on Product Qualification calls only.
    /// </summary>
    public string WsIspCode { get; set; } = string.Empty;

    /// <summary>
    /// Openserve <c>ISPID</c>, e.g. "WS MARUT" (space and casing preserved).
    /// Sent as the "ISP Identifier" serviceCharacteristic in every order
    /// payload AND as the FromLocation header on every Product Ordering
    /// call (create / query order details / cancel).
    /// </summary>
    public string IspIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// "ReplyToAddress" header sent on every Product Ordering call. The
    /// Postman collection defines it as "Callback URL that will be provided
    /// by Openserve" and Openserve supplied an Openserve-hosted value
    /// (e.g. https://stapitrx.openserve.co.za/ws-marut/productordercallback)
    /// — it is passed through verbatim and is NOT Smart Future's own
    /// /api/openserve/callback endpoint.
    /// </summary>
    public string ReplyToAddress { get; set; } = string.Empty;

    /// <summary>
    /// Smart Future's OWN inbound event endpoint (https://&lt;api-host&gt;/api/openserve/events),
    /// recorded here so the admin console can show exactly what to give
    /// Openserve. Never sent on any outbound call — the PDF says Openserve
    /// "will configure" the ISP's event endpoint; the Postman collection
    /// does not document how. Registration is out-of-band and unconfirmed.
    /// </summary>
    public string EventNotificationUrl { get; set; } = string.Empty;

    /// <summary>Openserve "SenderID" header sent on every call, e.g. "SMARTFUTURE".</summary>
    public string SenderId { get; set; } = "SmartFuture";

    /// <summary>Per-call timeout for outbound Openserve calls (clamped 5–120s by OpenserveApiClient).</summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    public OpenserveRetrySettings Retry { get; set; } = new();

    /// <summary>
    /// Reconciliation-worker poll interval for non-terminal orders
    /// (brief §15). Webhooks are the primary mechanism — this is the
    /// safety-net fallback, so keep it conservative.
    /// </summary>
    public int PollingFallbackIntervalMinutes { get; set; } = 30;

    public OpenserveCallbackAuthSettings CallbackAuth { get; set; } = new();

    /// <summary>True once every value the Postman collection sends on a Product Ordering call is present.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(WsIspCode)
        && !string.IsNullOrWhiteSpace(IspIdentifier)
        && !string.IsNullOrWhiteSpace(SenderId)
        && !string.IsNullOrWhiteSpace(ReplyToAddress);
}

public class OpenserveRetrySettings
{
    /// <summary>Max transport-level retry attempts for a single business submission (network/5xx only — never retries a business rejection).</summary>
    public int MaxAttempts { get; set; } = 3;

    public int BaseDelaySeconds { get; set; } = 5;
}

/// <summary>
/// Neither Openserve's PDF (which lists callback/event endpoint
/// authentication as "N/a") nor the provisioned Postman collection
/// documents any signature/HMAC/shared-secret scheme for inbound
/// callbacks or event notifications. <see cref="Mode"/> defaults to
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

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

    /// <summary>
    /// Submission recovery (OpenserveSubmissionRecoveryHostedService) — resending
    /// Retryable Create Order failures and the safety sweep for Fibre orders that
    /// never got a submission record. Distinct from reconciliation, which only
    /// polls orders Openserve already accepted. Config-only (appsettings / env
    /// vars <c>OpenserveFulfilment__SubmissionRecovery__*</c>).
    /// </summary>
    public OpenserveSubmissionRecoverySettings SubmissionRecovery { get; set; } = new();

    /// <summary>Product Qualification as the Fibre eligibility authority (coverage check + checkout gate). Config-only (<c>OpenserveFulfilment__Qualification__*</c>).</summary>
    public OpenserveQualificationSettings Qualification { get; set; } = new();

    public OpenserveCallbackAuthSettings CallbackAuth { get; set; } = new();

    /// <summary>
    /// True when SmartFuture can run authenticated Product Qualification —
    /// the integration is enabled and the qualification call has what it
    /// needs. While true, Openserve Product Qualification (not the public GIS
    /// lookup) decides Fibre eligibility for coverage checks and checkout.
    /// </summary>
    public bool CanQualify =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(WsIspCode);

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
/// Conservative defaults: a worker tick every 15 minutes resends only
/// Retryable failures (never sent / explicitly not processed), with
/// exponential backoff 15m → 30m → 1h → … capped at 24h, for at most 10
/// automatic resends (≈ 4 days, ending in daily attempts). The safety
/// sweep runs at start-up and then every 24h. Nothing here can make a
/// request whose outcome is unknown go out again.
/// </summary>
public class OpenserveSubmissionRecoverySettings
{
    /// <summary>Recovery on/off. Still gated by OpenserveFulfilment:Enabled — while the integration is disabled nothing runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Worker tick for resending due Retryable failures (clamped 5–1440).</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Automatic resends per failure streak (excluding the original attempt). 0 disables automatic resending.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>First backoff delay; doubles per automatic resend.</summary>
    public int BaseRetryDelayMinutes { get; set; } = 15;

    /// <summary>Backoff ceiling — keeps a long outage to one attempt per order per day.</summary>
    public int MaxRetryDelayMinutes { get; set; } = 1440;

    /// <summary>Safety sweep cadence (also runs once at start-up). Clamped 1–24 so it is at least daily.</summary>
    public int SafetySweepIntervalHours { get; set; } = 24;

    /// <summary>The sweep only considers orders whose network account was reserved within this many days.</summary>
    public int SafetySweepLookbackDays { get; set; } = 7;

    /// <summary>The sweep leaves orders younger than this alone, so it never races the automatic trigger.</summary>
    public int SafetySweepGraceMinutes { get; set; } = 15;

    /// <summary>
    /// Optional hard floor for the sweep (e.g. the go-live moment). The sweep also
    /// never looks before the last time an Admin switched the integration on, so
    /// orders paid while it was disabled (possibly ordered manually on the
    /// Openserve portal) are never sent automatically — Admin sends those by hand.
    /// </summary>
    public DateTime? SafetySweepNotBeforeUtc { get; set; }

    /// <summary>A claim (status Submitting) older than this is treated as interrupted — outcome unknown, never resent automatically.</summary>
    public int StaleSubmissionMinutes { get; set; } = 10;

    /// <summary>Upper bound on orders handled per worker pass, so a backlog never turns into a burst against Openserve.</summary>
    public int MaxOrdersPerRun { get; set; } = 25;

    /// <summary>
    /// When a submission finds no AMID it runs Product Qualification once first —
    /// but not again within this many minutes of the last (failed) attempt, so
    /// retries never hammer the qualification API. Admin "Run Product
    /// Qualification" ignores this.
    /// </summary>
    public int QualificationCooldownMinutes { get; set; } = 60;
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
public class OpenserveQualificationSettings
{
    /// <summary>
    /// A successful qualification for the exact same coordinates is reused for
    /// this long by coverage checks and the checkout gate, so a customer's
    /// coverage check → payment doesn't call Openserve twice (and repeated
    /// anonymous checks don't hammer it). 0 = always call.
    /// </summary>
    public int ReuseMinutes { get; set; } = 30;
}

public class OpenserveCallbackAuthSettings
{
    public OpenserveCallbackAuthMode Mode { get; set; } = OpenserveCallbackAuthMode.None;

    /// <summary>**Secret.** Used only when Mode requires a shared secret.</summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>Optional IP allowlist (CIDR or literal), used only when Mode includes IP checking.</summary>
    public string[] AllowedIpRanges { get; set; } = Array.Empty<string>();
}

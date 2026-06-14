namespace SmartFuture.Application.Payments.PayFast;

public class PayFastSettings
{
    /// <summary>
    /// Customer-facing kill-switch. When false (the default), the
    /// payment-gateway controller rejects customer-initiated PayFast
    /// requests with a friendly "temporarily unavailable" error.
    /// Initiator + webhook handler + tests stay resolvable from DI.
    /// </summary>
    public bool Enabled { get; set; } = false;

    public string MerchantId  { get; set; } = string.Empty;
    public string MerchantKey { get; set; } = string.Empty;
    public string Passphrase  { get; set; } = string.Empty;

    public bool UseSandbox { get; set; } = true;

    public string NotifyUrl  { get; set; } = string.Empty;
    public string ReturnUrl  { get; set; } = string.Empty;
    public string CancelUrl  { get; set; } = string.Empty;

    public bool UseTestAmountOverride { get; set; }
    public decimal? TestAmount { get; set; }

    // ─── Phase 1A — tokenization / recurring mandate capture ───────
    //
    // When enabled, the initiation flow adds `subscription_type=2` so
    // PayFast returns a reusable `token` in the ITN, which we capture as
    // a CustomerPaymentMandate. All default FALSE — current once-off
    // behaviour is byte-identical until explicitly turned on in UAT.
    // Phase 1A only CAPTURES the token; ad-hoc charging is Phase 1B.

    /// <summary>Master switch for PayFast tokenization. False → no tokenization on any flow.</summary>
    public bool TokenizationEnabled { get; set; } = false;

    /// <summary>Request tokenization on the order-intent / first-service-payment flow.</summary>
    public bool TokenizationForOrderIntentsEnabled { get; set; } = false;

    /// <summary>Request tokenization on the invoice-bound payment flow.</summary>
    public bool TokenizationForInvoicePaymentsEnabled { get; set; } = false;

    // ─── Phase 1B — recurring (ad-hoc) charge API ──────────────────
    //
    // Master gate for OUTGOING PayFast ad-hoc charge API calls
    // (POST /subscriptions/{token}/adhoc). Default FALSE — the recurring
    // charge service is registered but makes NO real PayFast API call
    // until this is turned on in UAT (Phase 1C wires the engine to it).

    /// <summary>When false, the PayFast recurring charge service never calls the ad-hoc API.</summary>
    public bool AdhocChargingEnabled { get; set; } = false;

    /// <summary>PayFast recurring/subscriptions API base URL (same host for sandbox + live).</summary>
    public string ApiBaseUrl { get; set; } = "https://api.payfast.co.za";

    /// <summary>PayFast API version header value.</summary>
    public string ApiVersion { get; set; } = "v1";

    /// <summary>
    /// When true, append <c>?testing=true</c> to ad-hoc API calls (sandbox).
    /// Null → mirror <see cref="UseSandbox"/> so a single sandbox toggle
    /// drives both the checkout and the API.
    /// </summary>
    public bool? UseSandboxApi { get; set; }

    /// <summary>Effective sandbox-API flag: explicit value, else mirrors <see cref="UseSandbox"/>.</summary>
    public bool EffectiveUseSandboxApi => UseSandboxApi ?? UseSandbox;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(MerchantId)
        && !string.IsNullOrWhiteSpace(MerchantKey)
        && !string.IsNullOrWhiteSpace(Passphrase)
        && !string.IsNullOrWhiteSpace(NotifyUrl);

    public string ProcessUrl => UseSandbox
        ? "https://sandbox.payfast.co.za/eng/process"
        : "https://www.payfast.co.za/eng/process";

    public string ValidateUrl => UseSandbox
        ? "https://sandbox.payfast.co.za/eng/query/validate"
        : "https://www.payfast.co.za/eng/query/validate";
}

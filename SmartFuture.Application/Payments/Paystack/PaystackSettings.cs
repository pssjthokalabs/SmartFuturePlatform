namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Paystack runtime configuration. Bound from the <c>"Paystack"</c>
/// configuration section (env-var prefix <c>Paystack__</c> in
/// production / docker / EAS).
///
/// Secrets MUST come from env vars / user-secrets / a key vault —
/// never commit the real <see cref="SecretKey"/> into appsettings.json.
/// The committed file only carries empty placeholders so a fresh
/// checkout can never silently start sending requests with no key.
///
/// Currently Paystack is the PRIMARY payment gateway for SmartFuture
/// (the only fully-approved provider as of 2026-05-29). Ozow and
/// PayFast remain registered for parallel testing, but Paystack is
/// the default both portal + mobile select.
///
/// **Hosted-checkout flow only.** The mobile/portal WebView and popup
/// open the <c>authorization_url</c> Paystack returns from
/// <c>POST /transaction/initialize</c>. <see cref="PublicKey"/> is
/// reserved for a future SDK/Inline checkout — the current backend
/// redirect flow does not use it.
/// </summary>
public class PaystackSettings
{
    /// <summary>
    /// Master kill-switch. When false, the initiator fails-fast with
    /// a clear "Paystack is not enabled" error and the webhook handler
    /// short-circuits without touching any payment state.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// **Secret.** Bearer token for every server-to-server Paystack
    /// call. Supply via env var / user-secrets only. Never logged.
    /// Test keys start with <c>sk_test_</c>; live keys with <c>sk_live_</c>.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional. Reserved for a future Paystack Inline / SDK flow.
    /// Not used by the current backend-initialize / hosted-redirect
    /// path. Safe to leave empty.
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Diagnostic hint surfaced in audit metadata. Inferred from the
    /// secret-key prefix (sk_test_ vs sk_live_) when the value is null,
    /// so a misconfigured flag does not silently flip the wrong way.
    /// </summary>
    public bool UseTestMode { get; set; } = true;

    /// <summary>
    /// Paystack transaction-initialize endpoint. Defaults to the
    /// production URL — they only have one (test vs live is keyed off
    /// the secret-key prefix). Override only when re-pointing to a
    /// proxy for diagnostics.
    /// </summary>
    public string InitializeUrl { get; set; } = "https://api.paystack.co/transaction/initialize";

    /// <summary>
    /// Verify-base URL. The handler appends <c>/{reference}</c>.
    /// Defaults to the production endpoint.
    /// </summary>
    public string VerifyBaseUrl { get; set; } = "https://api.paystack.co/transaction/verify";

    /// <summary>
    /// Per-transaction callback URL Paystack redirects the customer to
    /// after they close the hosted checkout. Sent on every initialize
    /// — Paystack honours this even when a different default is set on
    /// the dashboard. Required when <see cref="Enabled"/> is true.
    /// </summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>
    /// Customer-cancel landing page. Surfaced via
    /// <c>metadata.cancel_action</c> so the mobile WebView /
    /// portal popup can detect a deliberate cancel.
    /// </summary>
    public string CancelUrl { get; set; } = string.Empty;

    /// <summary>
    /// Informational only. Paystack POSTs webhooks to whatever URL is
    /// configured on the merchant dashboard; we mirror that value here
    /// so it shows up in <c>/api/diagnostics</c>. Has no effect on the
    /// server's behaviour.
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>ISO 4217. Defaults to ZAR.</summary>
    public string Currency { get; set; } = "ZAR";

    /// <summary>
    /// Optional. When set, restricts the channels Paystack offers in
    /// the hosted checkout. Leave empty to honour whatever the
    /// merchant has enabled on the Paystack dashboard. Do not hardcode
    /// unapproved channels here.
    /// </summary>
    public string[] AllowedChannels { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// Non-production R10 test-amount override, mirrors the Ozow
    /// pattern. Hard-blocked in Production regardless of the flag —
    /// the initiator checks <c>IHostEnvironment.IsProduction()</c>.
    /// </summary>
    public bool UseTestAmountOverride { get; set; }

    /// <summary>
    /// The amount (e.g. 10.00) sent to Paystack instead of the real
    /// invoice balance when <see cref="UseTestAmountOverride"/> is
    /// active. Payment.Amount is mirrored so the webhook amount check
    /// passes; the invoice stays at its original total and ends up
    /// PartiallyPaid until a real charge clears it — unless the
    /// payment apply path explicitly recognises the override
    /// (see <see cref="Dtos.ApplyPaymentStatusChangeRequestDto"/>).
    /// </summary>
    public decimal? TestAmount { get; set; }

    /// <summary>
    /// Secondary gate on <see cref="UseTestAmountOverride"/> when a
    /// LIVE secret key is in use. When false (the default), the live
    /// override is refused even on a non-production environment —
    /// avoids accidentally bleeding live-key R10 charges into a
    /// dev sandbox. Set true on UAT only after confirming the
    /// callback / webhook URLs point at the UAT API.
    ///
    /// HARD-BLOCKED in Production regardless of value.
    /// </summary>
    public bool AllowLiveTestAmountOverride { get; set; } = false;

    /// <summary>True once the minimum credentials needed to call Paystack are present.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(SecretKey)
        && !string.IsNullOrWhiteSpace(CallbackUrl);

    /// <summary>
    /// Best-effort test/live inference from the secret-key prefix.
    /// Surfaced in metadata + diagnostics so an operator can spot a
    /// mismatch between <see cref="UseTestMode"/> and the actual key
    /// in use. <c>true</c> when the key starts with <c>sk_test_</c>;
    /// <c>false</c> when <c>sk_live_</c>; null otherwise.
    /// </summary>
    public bool? IsTestKey =>
        string.IsNullOrWhiteSpace(SecretKey) ? null
        : SecretKey.StartsWith("sk_test_") ? true
        : SecretKey.StartsWith("sk_live_") ? false
        : (bool?)null;
}

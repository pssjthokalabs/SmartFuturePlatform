namespace SmartFuture.Application.Payments.Ozow;

/// <summary>
/// Phase 52 — Ozow Payments API settings. Bound from the
/// <c>"Ozow"</c> configuration section (env-var prefix
/// <c>Ozow__</c> in production / docker / EAS).
///
/// Secrets MUST come from env vars or a key vault — never check the
/// real values into appsettings.json. The repo only carries a
/// template with empty strings.
///
/// Hash calculation + URL choice live in <see cref="OzowHashCalculator"/>
/// and <see cref="OzowApiUrlResolver"/>; this class just carries
/// the raw values the merchant configured.
/// </summary>
public class OzowSettings
{
    /// <summary>Provided by Ozow per merchant — public-ish, sent on every request.</summary>
    public string SiteCode { get; set; } = string.Empty;

    /// <summary>HTTP header value <c>ApiKey: …</c>. Treat as a secret.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Appended to the hash input before SHA512. Treat as a secret.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2, defaults to ZA.</summary>
    public string CountryCode { get; set; } = "ZA";

    /// <summary>ISO 4217, defaults to ZAR.</summary>
    public string CurrencyCode { get; set; } = "ZAR";

    /// <summary>
    /// Sent as the <c>IsTest</c> field in the request body AND included
    /// (same value) in the HashCheck input — Ozow rejects a mismatch.
    ///
    /// Phase 53.2: NULLABLE + required. If the config key
    /// <c>Ozow:IsTest</c> is missing, options-binding leaves this null
    /// and DI validation refuses to start the app — so we never silently
    /// pick a default that may mismatch the merchant's account type.
    ///
    /// We don't have staging credentials right now, so set
    /// <c>Ozow__IsTest=false</c> in every environment until staging
    /// credentials are issued.
    /// </summary>
    public bool? IsTest { get; set; }

    /// <summary>Explicit API base URL. Defaults to the LIVE Ozow host
    /// (<c>https://api.ozow.com/PostPaymentRequest</c>) when null —
    /// we no longer auto-pick the staging host based on IsTest, because
    /// staging would silently use LIVE credentials that Ozow rejects.
    /// Override only when explicitly testing against staging with
    /// staging-issued keys.</summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// Public, internet-reachable URL that Ozow will POST the
    /// payment-completion webhook to. Must be set in any environment
    /// where Ozow is actually exercised. Empty string disables the
    /// initiator at boot (safer than silently sending a localhost URL
    /// that Ozow will never be able to reach).
    /// </summary>
    public string NotifyUrl { get; set; } = string.Empty;

    /// <summary>Pages the customer is redirected to after the Ozow checkout.</summary>
    public string SuccessUrl { get; set; } = string.Empty;
    public string CancelUrl  { get; set; } = string.Empty;
    public string ErrorUrl   { get; set; } = string.Empty;

    /// <summary>
    /// Set <c>Ozow__UseTestAmountOverride=true</c> to activate the
    /// test-amount override. Hard-blocked in Production — the
    /// initiator checks <c>IHostEnvironment.IsProduction()</c> and
    /// refuses to apply the override regardless of this flag.
    /// </summary>
    public bool UseTestAmountOverride { get; set; }

    /// <summary>
    /// The amount (e.g. 10.00) sent to Ozow instead of the real
    /// invoice balance when <see cref="UseTestAmountOverride"/> is
    /// active. Payment.Amount is mirrored so the webhook amount
    /// check passes; the invoice stays at its original total and
    /// ends up PartiallyPaid.
    /// </summary>
    public decimal? TestAmount { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SiteCode)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(NotifyUrl)
        && IsTest.HasValue;
}

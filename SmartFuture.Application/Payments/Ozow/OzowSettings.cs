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

    // ───────────────────────────────────────────────────────────────────
    // TEMPORARY LIVE OZOW TEST OVERRIDE.
    //
    // Set Ozow__TestAmountOverride=10 in UAT to force every Ozow
    // payment request to a small real-money test amount (e.g. R10)
    // regardless of the underlying invoice/payment amount. We mirror
    // the override onto the Payment row at initiate time so:
    //   - Ozow charges the override (R10).
    //   - SmartFuture.Payment.Amount = R10.
    //   - Webhook amount validation passes (R10 vs R10).
    //   - The R100 invoice ends up PartiallyPaid (R10 of R100) — i.e.
    //     it does NOT get marked Paid as if the full amount cleared.
    //     Matches the brief's safety requirement.
    //
    // Hard-gated:
    //   - Honoured only when IsTest=true. Live mode (IsTest=false)
    //     ignores the override completely — even if set — so a
    //     production deployment can't accidentally over-charge.
    //   - For live-key UAT (IsTest=false, real money), prefer setting
    //     the package's installation fee to a small amount (e.g. R10)
    //     in UAT data — keeps invoice/payment/Ozow amounts consistent.
    //   - Remove the env var before real production launch. The
    //     initiator logs "OZOW TEST AMOUNT OVERRIDE ACTIVE" on every
    //     initiation that hits this branch so operators can spot it.
    // ───────────────────────────────────────────────────────────────────
    public decimal? TestAmountOverride { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SiteCode)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(NotifyUrl)
        && IsTest.HasValue;
}

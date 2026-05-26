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
    /// When true the integration uses Ozow's staging API host AND
    /// sends <c>IsTest: true</c> in the request body. When false the
    /// production host is used and <c>IsTest: false</c> is sent.
    /// Mismatching these two is a common cause of "transaction
    /// declined" so we keep them coupled.
    /// </summary>
    public bool IsTest { get; set; } = true;

    /// <summary>Optional explicit override of the API base URL. Defaults to
    /// the standard staging/live host implied by <see cref="IsTest"/>.</summary>
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

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SiteCode)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(NotifyUrl);
}

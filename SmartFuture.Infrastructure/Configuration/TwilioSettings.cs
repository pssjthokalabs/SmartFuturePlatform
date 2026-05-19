namespace SmartFuture.Infrastructure.Configuration;

// Twilio runtime configuration. **Secrets** (AuthToken, ApiSecret) must
// come from environment variables / IIS app pool / dotnet user-secrets;
// the committed appsettings.json carries only structural placeholders.
//
// Env-var keys (double-underscore convention):
//
//   Twilio__AccountSid=<sid-or-blank>
//   Twilio__AuthToken=<secret>
//   Twilio__ApiKeySid=<optional-api-key-sid>
//   Twilio__ApiKeySecret=<secret>
//   Twilio__VerifyServiceSid=<verify-service-sid>
//   Twilio__SmsFromNumber=+27821234567
//   Twilio__WhatsAppFromNumber=whatsapp:+14155238886
//
// Treated as "not configured" when AccountSid is blank. In that state
// the SMS/WhatsApp/Verify services register their NotConfigured stubs
// which return ErrorCodes.SMS_NOT_CONFIGURED / PROVIDER_NOT_CONFIGURED.
public class TwilioSettings
{
    public const string SectionName = "Twilio";

    /// <summary>Twilio account SID. Blank => Twilio is not configured.</summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>**Secret.** Supply via env var / user-secrets only.</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>Optional API key SID. When set, used in preference to AccountSid+AuthToken.</summary>
    public string ApiKeySid { get; set; } = string.Empty;

    /// <summary>**Secret.** Supply via env var / user-secrets only.</summary>
    public string ApiKeySecret { get; set; } = string.Empty;

    /// <summary>Twilio Verify service SID. Treated as non-secret (can live in env or appsettings).</summary>
    public string VerifyServiceSid { get; set; } = string.Empty;

    /// <summary>E.164 SMS sender number, e.g. +27821234567.</summary>
    public string SmsFromNumber { get; set; } = string.Empty;

    /// <summary>WhatsApp sender, e.g. "whatsapp:+14155238886".</summary>
    public string WhatsAppFromNumber { get; set; } = string.Empty;

    /// <summary>
    /// Returns true when the minimum credentials needed to call
    /// Twilio are present. Stubs use this to decide whether to
    /// register real providers or NotConfigured fallbacks.
    /// </summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(AccountSid)
           && (!string.IsNullOrWhiteSpace(AuthToken)
               || (!string.IsNullOrWhiteSpace(ApiKeySid) && !string.IsNullOrWhiteSpace(ApiKeySecret)));
}

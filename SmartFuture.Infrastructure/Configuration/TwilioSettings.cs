namespace SmartFuture.Infrastructure.Configuration;

// Twilio runtime configuration — Phase 35C nested shape.
//
// **Secrets** (AuthToken, ApiKeySecret, Verify ServiceSid if your
// account treats it as private) come from environment variables /
// IIS app pool / dotnet user-secrets. The committed appsettings.json
// carries only empty placeholders.
//
// Env-var keys (double-underscore convention):
//
//   Twilio__AccountSid=<sid>
//   Twilio__AuthToken=<secret>
//   Twilio__ApiKeySid=<optional-api-key-sid>
//   Twilio__ApiKeySecret=<secret>
//
//   Twilio__Sms__FromPhoneNumber=+27821234567
//   Twilio__Sms__MessagingServiceSid=<optional-messaging-service-sid>
//
//   Twilio__WhatsApp__FromPhoneNumber=whatsapp:+14155238886
//   Twilio__WhatsApp__MessagingServiceSid=<optional>
//   Twilio__WhatsApp__DefaultContentSid=<optional-template-sid>
//
//   Twilio__Verify__ServiceSid=<verify-service-sid>
//   Twilio__Verify__ResendCooldownSeconds=30
//
// Treated as "not configured" when AccountSid is blank OR neither an
// AuthToken nor an ApiKey pair is supplied. In that state the
// SMS / WhatsApp / PhoneVerification services fall back to the
// NotConfigured stubs which return PROVIDER_NOT_CONFIGURED /
// SMS_NOT_CONFIGURED — same surface code as today.
public class TwilioSettings
{
    public const string SectionName = "Twilio";

    /// <summary>Twilio account SID. Blank => Twilio is not configured.</summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>**Secret.** Supply via env var / user-secrets only.</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>Optional API key SID. Preferred over AccountSid+AuthToken long-term.</summary>
    public string ApiKeySid { get; set; } = string.Empty;

    /// <summary>**Secret.** Supply via env var / user-secrets only.</summary>
    public string ApiKeySecret { get; set; } = string.Empty;

    public TwilioSmsSettings Sms { get; set; } = new();
    public TwilioWhatsAppSettings WhatsApp { get; set; } = new();
    public TwilioVerifySettings Verify { get; set; } = new();

    /// <summary>
    /// Returns true when the minimum credentials needed to call
    /// Twilio are present. Concrete providers branch on this; stubs
    /// continue to be used when false.
    /// </summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(AccountSid)
           && (!string.IsNullOrWhiteSpace(AuthToken)
               || (!string.IsNullOrWhiteSpace(ApiKeySid) && !string.IsNullOrWhiteSpace(ApiKeySecret)));
}

public class TwilioSmsSettings
{
    /// <summary>E.164 SMS sender number, e.g. +27821234567. Required unless MessagingServiceSid is set.</summary>
    public string FromPhoneNumber { get; set; } = string.Empty;

    /// <summary>Optional Messaging Service SID — preferred for prod (handles failover, alphanumeric sender id, etc.).</summary>
    public string MessagingServiceSid { get; set; } = string.Empty;
}

public class TwilioWhatsAppSettings
{
    /// <summary>WhatsApp sender, e.g. "whatsapp:+14155238886". Required unless MessagingServiceSid is set.</summary>
    public string FromPhoneNumber { get; set; } = string.Empty;

    /// <summary>Optional WhatsApp Messaging Service SID.</summary>
    public string MessagingServiceSid { get; set; } = string.Empty;

    /// <summary>Optional default approved template (Content) SID for free-form WhatsApp messages.</summary>
    public string DefaultContentSid { get; set; } = string.Empty;
}

public class TwilioVerifySettings
{
    /// <summary>Twilio Verify service SID — required for Verify-backed OTP send/check.</summary>
    public string ServiceSid { get; set; } = string.Empty;

    /// <summary>Minimum seconds between two resend requests for the same number. Defaults to 30.</summary>
    public int ResendCooldownSeconds { get; set; } = 30;
}

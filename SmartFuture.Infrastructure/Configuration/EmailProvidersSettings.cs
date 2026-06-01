using SmartFuture.Shared.Enums.Communication;

namespace SmartFuture.Infrastructure.Configuration;

// Multi-sender SMTP pool configuration. Bound to the
// `EmailProviders` section of appsettings + env vars.
//
// Production host: notify.smartfuture.co.za (SmarterASP/SmarterMail).
//   Plain SMTP-AUTH on mail.notify.smartfuture.co.za:8889 (EnableSsl=false).
//   SSL alternative: mail5018.site4now.net:465 or :587 (EnableSsl=true) —
//   not used by default; only swap in if the plain path becomes
//   unavailable.
//
// Each category gets its own SmarterMail mailbox so customer replies
// land on the right team. The committed appsettings.json carries the
// non-secret host/port/EnableSsl/Username/FromEmail/FromName values
// directly so a fresh deployment only needs the per-sender
// **Password** secrets supplied.
//
// **Secrets**: `Password` MUST come from environment variables /
// IIS app pool / dotnet user-secrets. Usernames are non-secret and
// stay in appsettings.json — overriding via env var is still
// supported if a deployment wants to point at different mailboxes.
//
// Env-var keys (double-underscore convention):
//
//   EmailProviders__Senders__NoReply__Password=<secret>
//   EmailProviders__Senders__Support__Password=<secret>
//   EmailProviders__Senders__Accounts__Password=<secret>
//   EmailProviders__Senders__Payments__Password=<secret>
//   EmailProviders__Senders__Security__Password=<secret>
//
// If a sender's Username is left blank, the multi-sender SMTP sender
// falls back to the `Default` sender's credentials. With per-category
// mailboxes on notify.smartfuture.co.za each entry carries its own
// Username, so the fallback only fires if config is incomplete — and
// a warning is logged when it does (see SmtpMultiSenderEmailSender).
public class EmailProvidersSettings
{
    public const string SectionName = "EmailProviders";

    /// <summary>
    /// Which sender to use when callers don't specify one. Maps to a
    /// key in <see cref="Senders"/>; defaults to <see cref="EmailSenderType.NoReply"/>.
    /// </summary>
    public EmailSenderType DefaultSender { get; set; } = EmailSenderType.NoReply;

    /// <summary>
    /// Per-sender SMTP + identity configuration. Keys are the string
    /// form of <see cref="EmailSenderType"/> (case-insensitive at bind
    /// time). A missing key falls back to <c>Default</c> at send time.
    /// </summary>
    public Dictionary<string, EmailSenderConfig> Senders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class EmailSenderConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;

    /// <summary>
    /// SMTP-AUTH username. Often a licensed mailbox address on
    /// Microsoft 365. Leave blank to inherit from the Default sender.
    /// Secrets MUST be supplied via environment.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// SMTP-AUTH password. **NEVER commit a real value.** Supply via
    /// env var / app-pool / user-secrets only.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The visible "From" address on outbound mail. For shared
    /// Microsoft 365 mailboxes this can differ from <see cref="Username"/>
    /// provided Send-As permission is granted.
    /// </summary>
    public string FromEmail { get; set; } = string.Empty;

    /// <summary>
    /// The display name shown next to the From address.
    /// </summary>
    public string FromName { get; set; } = "Smart Future";
}

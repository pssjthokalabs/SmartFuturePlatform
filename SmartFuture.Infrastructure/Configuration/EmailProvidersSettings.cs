using SmartFuture.Shared.Enums.Communication;

namespace SmartFuture.Infrastructure.Configuration;

// Multi-sender SMTP pool configuration. Bound to the
// `EmailProviders` section of appsettings + env vars. Each sender
// shares the same SMTP host/port/SSL settings in practice (Microsoft
// 365 → smtp.office365.com:587/STARTTLS), but is allowed to have its
// own Username/Password so that licensed mailboxes can authenticate
// individually when SMTP AUTH is enabled per mailbox.
//
// **Secrets**: `Password` and (optionally) `Username` must come from
// environment variables / IIS app pool / dotnet user-secrets. The
// committed appsettings.json carries only host/port/SSL/FromEmail/
// FromName placeholders.
//
// Env-var keys (double-underscore convention):
//
//   EmailProviders__Senders__NoReply__Username=<licensed-smtp-user>
//   EmailProviders__Senders__NoReply__Password=<secret>
//   EmailProviders__Senders__Support__Username=<licensed-smtp-user>
//   EmailProviders__Senders__Support__Password=<secret>
//   …etc for Accounts / Payments / Security / Default
//
// If a sender's Username is left blank, the multi-sender SMTP sender
// falls back to the `Default` sender's credentials (handy when one
// licensed mailbox holds Send-As permissions for all shared
// mailboxes).
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

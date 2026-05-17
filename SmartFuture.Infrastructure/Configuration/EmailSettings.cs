namespace SmartFuture.Infrastructure.Configuration;

// Email delivery configuration.
//
// `Provider` selects which `INotificationSender` is wired up at startup:
//
//   "Logging" — LoggingNotificationSender (current default). Writes
//               every outbound message to the application log only. Safe
//               for local dev with no SMTP credentials.
//
//   "Smtp"    — SmtpEmailSender. Hands Email-channel messages off to an
//               SMTP server using `System.Net.Mail.SmtpClient`. Non-email
//               channels (SMS / Push / System) fall back to logging so
//               that swapping the provider doesn't silently break them.
//
// SMTP credentials should never live in committed config. Use
// environment variables or user-secrets:
//   EmailSettings__Smtp__Host=smtp.example.com
//   EmailSettings__Smtp__Username=...
//   EmailSettings__Smtp__Password=...
public class EmailSettings
{
    public const string SectionName = "EmailSettings";

    public string Provider { get; set; } = "Logging";
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Smart Future";

    public SmtpSettings Smtp { get; set; } = new();
}

public class SmtpSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

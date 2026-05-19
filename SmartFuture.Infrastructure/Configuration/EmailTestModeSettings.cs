namespace SmartFuture.Infrastructure.Configuration;

// Phase 35D — one-mailbox SMTP test-mode override.
//
// When `Enabled=true`, every outbound notification — regardless of
// the request's SenderType (Security / Support / Payments / …) — is
// dispatched through a single SMTP account using these settings.
// Useful for local development and UAT debugging where the
// multi-sender pool or production mailbox configuration would
// otherwise need to be in place.
//
// **Selection priority** (see ServiceExtensions.AddEmailServices):
//
//   1. `EmailTestMode:Enabled = true`  → `TestModeSmtpNotificationSender`
//   2. `EmailSettings:Provider = MultiSmtp` → `SmtpMultiSenderEmailSender`
//   3. `EmailSettings:Provider = Smtp`      → `SmtpEmailSender`
//   4. anything else (or unset)             → `LoggingNotificationSender`
//
// **Secrets**: `Password` MUST come from environment variables /
// IIS app pool / dotnet user-secrets. Never commit a real password
// to appsettings.*.json. `Username` is also recommended to live in
// env vars, though a non-secret service mailbox address can live in
// dev config if that helps your workflow.
//
// **Production policy**: keep `EmailTestMode__Enabled=false` in
// Production app-pool variables. The startup banner prints the
// resolved value at every boot so a forgotten override is obvious.
public class EmailTestModeSettings
{
    public const string SectionName = "EmailTestMode";

    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;

    /// <summary>SMTP-AUTH username. Often a licensed mailbox address.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>**Secret.** Supply via env var / user-secrets / app-pool only.</summary>
    public string Password { get; set; } = string.Empty;

    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Smart Future Test";
}

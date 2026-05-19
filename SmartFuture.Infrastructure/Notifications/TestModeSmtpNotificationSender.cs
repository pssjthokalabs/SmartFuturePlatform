using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Infrastructure.Configuration;
using SmartFuture.Shared.Enums.Notifications;

namespace SmartFuture.Infrastructure.Notifications;

// Phase 35D — one-mailbox test-mode SMTP sender.
//
// All outbound `NotificationChannel.Email` traffic is routed through
// the single SMTP account configured in `EmailTestMode`. The request's
// `SenderType` (Security / Support / Payments / …) is intentionally
// **ignored** — every email leaves from `EmailTestMode.FromEmail`. This
// removes the multi-sender pool entirely while debugging or doing
// local testing; the production providers stay registered but the
// factory in `AddEmailServices` skips them when test mode is enabled.
//
// Non-email channels (SMS / Push / System) fall back to a logging
// record so that swapping in this sender doesn't silently fail when
// some future code path tries to send an SMS through the notification
// service.
//
// **Secrets**: the SMTP password is read once from configuration into
// `SmtpClient.Credentials`. It is never logged. Pre-send and failure
// logs only carry host/port/recipient — no credentials, no OTP code,
// no full HTML body.
public class TestModeSmtpNotificationSender : INotificationSender
{
    private const string ProviderName = "TestModeSmtpNotificationSender";
    private const string LoggingProviderName = "TestModeSmtpNotificationSender(fallback:logging)";
    private const int BodySnippetLength = 200;

    private readonly EmailTestModeSettings _settings;
    private readonly ILogger<TestModeSmtpNotificationSender> _logger;

    public TestModeSmtpNotificationSender(
        IOptions<EmailTestModeSettings> settings,
        ILogger<TestModeSmtpNotificationSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<NotificationSendResult> SendAsync(
        SendNotificationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        // Non-email channels: log + succeed so SMS/Push are not
        // marked as failed when no real provider is wired up yet.
        // Mirrors the policy in the other SMTP senders.
        if (request.Channel != NotificationChannel.Email)
        {
            LogFallback(request);
            return NotificationSendResult.Succeeded(LoggingProviderName, Guid.NewGuid().ToString("N"));
        }

        // Hard-wired test sender (Phase 35D-fix): EVERY required field
        // is checked up-front and reported with the missing config-key
        // name. This is the only sender on the active DI graph, so
        // returning FailedResult here is the only safe failure mode —
        // we must never silently log-and-succeed.
        if (string.IsNullOrWhiteSpace(_settings.FromEmail))
        {
            const string msg = "EmailTestMode:FromEmail is not configured.";
            _logger.LogError(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }
        if (string.IsNullOrWhiteSpace(_settings.Host))
        {
            const string msg = "EmailTestMode:Host is not configured.";
            _logger.LogError(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }
        if (string.IsNullOrWhiteSpace(_settings.Username))
        {
            const string msg = "EmailTestMode:Username is not configured. Supply the SMTP-AUTH user via env var / user-secrets.";
            _logger.LogError(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }
        if (string.IsNullOrWhiteSpace(_settings.Password))
        {
            const string msg = "EmailTestMode:Password is not configured. Supply the SMTP-AUTH password via env var / user-secrets.";
            _logger.LogError(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }
        if (string.IsNullOrWhiteSpace(request.RecipientEmail))
        {
            const string msg = "RecipientEmail is required for the Email channel.";
            _logger.LogWarning(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }

        // Pre-send diagnostic. Logged BEFORE SendMailAsync so an
        // attempt is visible even if SMTP later throws or hangs.
        // Password is never included.
        _logger.LogInformation(
            "[Notification:{Provider}] Attempting SMTP send. From={FromEmail} Recipient={Recipient} " +
            "Subject={Subject} IsHtml={IsHtml} Host={Host} Port={Port} EnableSsl={Ssl} " +
            "UsernamePresent={UsernamePresent} PasswordPresent={PasswordPresent} " +
            "RequestedSenderTypeIgnored={SenderType}",
            ProviderName, _settings.FromEmail, request.RecipientEmail, request.Subject,
            request.IsHtml, _settings.Host, _settings.Port, _settings.EnableSsl,
            !string.IsNullOrWhiteSpace(_settings.Username),
            !string.IsNullOrWhiteSpace(_settings.Password),
            request.SenderType);

        using var smtpClient = BuildSmtpClient();
        using var mailMessage = BuildMailMessage(request);

        try
        {
            await smtpClient.SendMailAsync(mailMessage, cancellationToken);

            _logger.LogInformation(
                "[Notification:{Provider}] From={FromEmail} Recipient={Recipient} Subject={Subject} sent via {Host}:{Port}",
                ProviderName, _settings.FromEmail, request.RecipientEmail, request.Subject, _settings.Host, _settings.Port);

            return NotificationSendResult.Succeeded(ProviderName, Guid.NewGuid().ToString("N"));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "[Notification:{Provider}] Send cancelled for Recipient={Recipient} Subject={Subject}",
                ProviderName, request.RecipientEmail, request.Subject);
            return NotificationSendResult.FailedResult(ProviderName, "SMTP send was cancelled.");
        }
        catch (Exception ex)
        {
            // Failure log carries only host/port/recipient — never credentials.
            _logger.LogError(ex,
                "[Notification:{Provider}] SMTP send failed Recipient={Recipient} via {Host}:{Port}",
                ProviderName, request.RecipientEmail, _settings.Host, _settings.Port);
            return NotificationSendResult.FailedResult(ProviderName, $"SMTP send failed: {ex.Message}");
        }
    }

    private SmtpClient BuildSmtpClient()
    {
        var client = new SmtpClient(_settings.Host, _settings.Port)
        {
            EnableSsl = _settings.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };

        if (!string.IsNullOrWhiteSpace(_settings.Username))
        {
            client.Credentials = new NetworkCredential(_settings.Username, _settings.Password ?? string.Empty);
        }

        return client;
    }

    private MailMessage BuildMailMessage(SendNotificationRequestDto request)
    {
        var fromName = string.IsNullOrWhiteSpace(_settings.FromName) ? "Smart Future Test" : _settings.FromName;
        var from = new MailAddress(_settings.FromEmail, fromName);
        var to = new MailAddress(request.RecipientEmail!);

        var message = new MailMessage(from, to)
        {
            Subject = request.Subject ?? string.Empty
        };

        if (request.IsHtml && !string.IsNullOrEmpty(request.HtmlBody))
        {
            // HTML body + plain-text AlternateView so non-HTML clients
            // still see a useful message.
            message.Body = request.HtmlBody;
            message.IsBodyHtml = true;
            if (!string.IsNullOrEmpty(request.Body))
            {
                message.AlternateViews.Add(
                    AlternateView.CreateAlternateViewFromString(request.Body, null, "text/plain"));
                message.AlternateViews.Add(
                    AlternateView.CreateAlternateViewFromString(request.HtmlBody, null, "text/html"));
            }
        }
        else
        {
            message.Body = request.Body ?? string.Empty;
            message.IsBodyHtml = false;
        }

        return message;
    }

    private void LogFallback(SendNotificationRequestDto request)
    {
        var recipient = request.Channel switch
        {
            NotificationChannel.Sms => request.RecipientPhone,
            _ => request.RecipientEmail ?? request.RecipientPhone
        };

        var bodyPreview = string.IsNullOrEmpty(request.Body)
            ? string.Empty
            : (request.Body.Length <= BodySnippetLength
                ? request.Body
                : request.Body[..BodySnippetLength] + "...");

        _logger.LogInformation(
            "[Notification:{Provider}] Type={Type} Channel={Channel} Recipient={Recipient} Subject={Subject} BodyPreview={BodyPreview} (logged — channel not supported by SMTP sender)",
            LoggingProviderName, request.Type, request.Channel, recipient, request.Subject, bodyPreview);
    }
}

using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Infrastructure.Configuration;
using SmartFuture.Shared.Enums.Notifications;

namespace SmartFuture.Infrastructure.Notifications;

// SMTP-backed implementation of INotificationSender.
//
// Scope:
//  * `NotificationChannel.Email` is dispatched through `SmtpClient`.
//  * Every other channel (Sms / Push / System) falls back to a logging
//    record so that swapping the provider in DI does not silently drop
//    SMS or push notifications when their dedicated providers aren't
//    integrated yet.
//
// Configuration failures (missing host, missing FromEmail, etc.) are
// reported as `Failed` send results — the NotificationService persists
// the failure to `OutboundNotifications`, where ops can see what tried
// to go out. Callers like `AuthService.ForgotPasswordAsync` deliberately
// swallow notification failures into the safe "if the email is
// registered…" response to avoid leaking account-existence information.
public class SmtpEmailSender : INotificationSender
{
    private const string ProviderName = "SmtpEmailSender";
    private const string LoggingProviderName = "SmtpEmailSender(fallback:logging)";
    private const int BodySnippetLength = 200;

    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailSettings> settings, ILogger<SmtpEmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<NotificationSendResult> SendAsync(
        SendNotificationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        // Non-email channels: log + succeed. Treating these as failures
        // would create noise in the OutboundNotifications failure metrics
        // for transports we never intended to send via SMTP.
        if (request.Channel != NotificationChannel.Email)
        {
            LogFallback(request);
            return NotificationSendResult.Succeeded(LoggingProviderName, Guid.NewGuid().ToString("N"));
        }

        if (string.IsNullOrWhiteSpace(_settings.FromEmail))
        {
            const string msg = "EmailSettings:FromEmail is not configured.";
            _logger.LogError(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }
        if (string.IsNullOrWhiteSpace(_settings.Smtp.Host))
        {
            const string msg = "EmailSettings:Smtp:Host is not configured.";
            _logger.LogError(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }
        if (string.IsNullOrWhiteSpace(request.RecipientEmail))
        {
            const string msg = "RecipientEmail is required for the Email channel.";
            _logger.LogWarning(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }

        // Pre-send diagnostic so an attempt is visible even if SendMailAsync
        // throws/hangs. Password is never logged.
        _logger.LogInformation(
            "[Notification:{Provider}] Attempting SMTP send. From={FromEmail} Recipient={Recipient} " +
            "Subject={Subject} IsHtml={IsHtml} Host={Host} Port={Port} EnableSsl={Ssl} " +
            "UsernamePresent={UsernamePresent} PasswordPresent={PasswordPresent}",
            ProviderName, _settings.FromEmail, request.RecipientEmail, request.Subject,
            request.IsHtml, _settings.Smtp.Host, _settings.Smtp.Port, _settings.Smtp.EnableSsl,
            !string.IsNullOrWhiteSpace(_settings.Smtp.Username),
            !string.IsNullOrWhiteSpace(_settings.Smtp.Password));

        using var smtpClient = BuildSmtpClient();
        using var mailMessage = BuildMailMessage(request);

        try
        {
            // SmtpClient.SendMailAsync accepts a CancellationToken on .NET 5+.
            // The token aborts the connection rather than waiting for the
            // server to finish, which is the behaviour we want when the
            // hosting environment cancels.
            await smtpClient.SendMailAsync(mailMessage, cancellationToken);

            _logger.LogInformation(
                "[Notification:{Provider}] Type={Type} Channel={Channel} Recipient={Recipient} Subject={Subject} sent via SMTP host {Host}",
                ProviderName, request.Type, request.Channel,
                request.RecipientEmail, request.Subject, _settings.Smtp.Host);

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
            // Log without the SMTP password — the SmtpClient settings object
            // is not included in the log message; only host/port/recipient.
            _logger.LogError(ex,
                "[Notification:{Provider}] SMTP send failed for Recipient={Recipient} via {Host}:{Port}",
                ProviderName, request.RecipientEmail, _settings.Smtp.Host, _settings.Smtp.Port);
            return NotificationSendResult.FailedResult(ProviderName, $"SMTP send failed: {ex.Message}");
        }
    }

    private SmtpClient BuildSmtpClient()
    {
        var client = new SmtpClient(_settings.Smtp.Host, _settings.Smtp.Port)
        {
            EnableSsl = _settings.Smtp.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };

        if (!string.IsNullOrWhiteSpace(_settings.Smtp.Username))
        {
            client.Credentials = new NetworkCredential(_settings.Smtp.Username, _settings.Smtp.Password ?? string.Empty);
        }

        return client;
    }

    private MailMessage BuildMailMessage(SendNotificationRequestDto request)
    {
        var fromName = string.IsNullOrWhiteSpace(_settings.FromName) ? "Smart Future" : _settings.FromName;
        var from = new MailAddress(_settings.FromEmail, fromName);
        var to = new MailAddress(request.RecipientEmail!);

        var message = new MailMessage(from, to)
        {
            Subject = request.Subject ?? string.Empty
        };

        // Phase 35C — single SMTP sender now supports HTML so the
        // existing AuthEmailTemplates / OrderEmailTemplates render
        // correctly without needing the multi-sender pool. Plain text
        // body is sent as an AlternateView so non-HTML clients still
        // see a useful message; SenderType is intentionally ignored
        // here — all mail uses EmailSettings.FromEmail/FromName.
        if (request.IsHtml && !string.IsNullOrEmpty(request.HtmlBody))
        {
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

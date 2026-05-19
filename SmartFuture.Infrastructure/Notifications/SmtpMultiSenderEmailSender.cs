using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Infrastructure.Configuration;
using SmartFuture.Shared.Enums.Communication;
using SmartFuture.Shared.Enums.Notifications;

namespace SmartFuture.Infrastructure.Notifications;

// Multi-sender SMTP implementation of INotificationSender (Phase 35).
//
// Picks the logical sender from `SendNotificationRequestDto.SenderType`
// — falling back to `EmailProviders:DefaultSender` then to the first
// configured sender — and dispatches an HTML email (with the plain
// `Body` as the AlternateView) when `IsHtml=true`. Otherwise sends a
// plain-text-only email, preserving the existing single-sender
// behaviour for callers that haven't migrated.
//
// Non-email channels (SMS / Push / System) fall back to logging so
// that a future Twilio/Push provider can be slotted in without this
// sender silently swallowing them.
//
// **Secrets**: SMTP usernames/passwords come from configuration that
// resolves env vars + user-secrets. This class never logs the
// password value — even on send failure only host/port/recipient are
// included in the diagnostic message.
public class SmtpMultiSenderEmailSender : INotificationSender
{
    private const string ProviderName = "SmtpMultiSenderEmailSender";
    private const string LoggingProviderName = "SmtpMultiSenderEmailSender(fallback:logging)";
    private const int BodySnippetLength = 200;

    private readonly EmailProvidersSettings _settings;
    private readonly ILogger<SmtpMultiSenderEmailSender> _logger;

    public SmtpMultiSenderEmailSender(IOptions<EmailProvidersSettings> settings, ILogger<SmtpMultiSenderEmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<NotificationSendResult> SendAsync(
        SendNotificationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        // Non-email channels: log + succeed so SMS/Push are not
        // marked as failed when a real provider isn't wired up yet.
        if (request.Channel != NotificationChannel.Email)
        {
            LogFallback(request);
            return NotificationSendResult.Succeeded(LoggingProviderName, Guid.NewGuid().ToString("N"));
        }

        if (string.IsNullOrWhiteSpace(request.RecipientEmail))
        {
            const string msg = "RecipientEmail is required for the Email channel.";
            _logger.LogWarning(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }

        var (sender, defaultSender) = ResolveSenders(request.SenderType);
        if (sender is null)
        {
            var msg = $"EmailProviders:Senders is empty — no sender configured for {request.SenderType}.";
            _logger.LogError(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }

        if (string.IsNullOrWhiteSpace(sender.Host))
        {
            var msg = $"EmailProviders:Senders:{request.SenderType}:Host is not configured.";
            _logger.LogError(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }
        if (string.IsNullOrWhiteSpace(sender.FromEmail))
        {
            var msg = $"EmailProviders:Senders:{request.SenderType}:FromEmail is not configured.";
            _logger.LogError(msg);
            return NotificationSendResult.FailedResult(ProviderName, msg);
        }

        // Pre-send diagnostic. Logged BEFORE SendMailAsync so an
        // attempt is visible even if the SMTP call later throws or
        // hangs. Password is never included; the credentials live on
        // SmtpClient and are not part of this log scope.
        var resolvedUsernamePresent = !string.IsNullOrWhiteSpace(sender.Username)
            || !string.IsNullOrWhiteSpace(defaultSender?.Username);
        var resolvedPasswordPresent = !string.IsNullOrWhiteSpace(sender.Password)
            || !string.IsNullOrWhiteSpace(defaultSender?.Password);

        _logger.LogInformation(
            "[Notification:{Provider}] Attempting SMTP send. Sender={SenderType} From={FromEmail} " +
            "Recipient={Recipient} Subject={Subject} IsHtml={IsHtml} Host={Host} Port={Port} EnableSsl={Ssl} " +
            "UsernamePresent={UsernamePresent} PasswordPresent={PasswordPresent}",
            ProviderName, request.SenderType, sender.FromEmail, request.RecipientEmail, request.Subject,
            request.IsHtml, sender.Host, sender.Port, sender.EnableSsl,
            resolvedUsernamePresent, resolvedPasswordPresent);

        using var smtpClient = BuildSmtpClient(sender, defaultSender);
        using var mailMessage = BuildMailMessage(request, sender);

        try
        {
            await smtpClient.SendMailAsync(mailMessage, cancellationToken);

            _logger.LogInformation(
                "[Notification:{Provider}] Sender={SenderType} From={FromEmail} Recipient={Recipient} Subject={Subject} sent via {Host}:{Port}",
                ProviderName, request.SenderType, sender.FromEmail, request.RecipientEmail, request.Subject, sender.Host, sender.Port);

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
            // Never include credentials in the failure log.
            _logger.LogError(ex,
                "[Notification:{Provider}] SMTP send failed Sender={SenderType} Recipient={Recipient} via {Host}:{Port}",
                ProviderName, request.SenderType, request.RecipientEmail, sender.Host, sender.Port);
            return NotificationSendResult.FailedResult(ProviderName, $"SMTP send failed: {ex.Message}");
        }
    }

    // Returns (selectedSender, defaultSender). The default is used as a
    // credentials fallback when the selected sender doesn't have its own
    // SMTP user — common when one licensed mailbox holds Send-As rights
    // for all shared mailboxes.
    private (EmailSenderConfig? selected, EmailSenderConfig? defaultSender) ResolveSenders(EmailSenderType requested)
    {
        // `EmailSenderType.Default` is a sentinel — let the configured
        // `DefaultSender` mapping pick the actual identity.
        var effective = requested == EmailSenderType.Default
            ? _settings.DefaultSender
            : requested;

        var defaultSender = LookupSender(_settings.DefaultSender)
            ?? LookupSender(EmailSenderType.Default)
            ?? LookupSender(EmailSenderType.NoReply);

        var selected = LookupSender(effective) ?? defaultSender;
        return (selected, defaultSender);
    }

    private EmailSenderConfig? LookupSender(EmailSenderType type)
        => _settings.Senders is { Count: > 0 } && _settings.Senders.TryGetValue(type.ToString(), out var s)
            ? s
            : null;

    private SmtpClient BuildSmtpClient(EmailSenderConfig sender, EmailSenderConfig? fallback)
    {
        var client = new SmtpClient(sender.Host, sender.Port)
        {
            EnableSsl = sender.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };

        var username = string.IsNullOrWhiteSpace(sender.Username) ? fallback?.Username : sender.Username;
        var password = string.IsNullOrWhiteSpace(sender.Password) ? fallback?.Password : sender.Password;

        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password ?? string.Empty);
        }

        return client;
    }

    private static MailMessage BuildMailMessage(SendNotificationRequestDto request, EmailSenderConfig sender)
    {
        var fromName = string.IsNullOrWhiteSpace(sender.FromName) ? "Smart Future" : sender.FromName;
        var from = new MailAddress(sender.FromEmail, fromName);
        var to = new MailAddress(request.RecipientEmail!);

        var message = new MailMessage(from, to)
        {
            Subject = request.Subject ?? string.Empty
        };

        if (request.IsHtml && !string.IsNullOrEmpty(request.HtmlBody))
        {
            // Send HTML as the primary body with the plain text Body
            // as the AlternateView so non-HTML clients still see a
            // useful message.
            message.Body = request.HtmlBody;
            message.IsBodyHtml = true;
            if (!string.IsNullOrEmpty(request.Body))
            {
                var plainView = AlternateView.CreateAlternateViewFromString(
                    request.Body, null, "text/plain");
                var htmlView = AlternateView.CreateAlternateViewFromString(
                    request.HtmlBody, null, "text/html");
                message.AlternateViews.Add(plainView);
                message.AlternateViews.Add(htmlView);
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

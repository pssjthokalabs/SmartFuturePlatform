using Microsoft.Extensions.Logging;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Shared.Enums.Notifications;

namespace SmartFuture.Infrastructure.Notifications;

public class LoggingNotificationSender : INotificationSender
{
    private const string ProviderName = "LoggingNotificationSender";
    private const int BodySnippetLength = 200;

    private readonly ILogger<LoggingNotificationSender> _logger;

    public LoggingNotificationSender(ILogger<LoggingNotificationSender> logger)
    {
        _logger = logger;
    }

    public Task<NotificationSendResult> SendAsync(
        SendNotificationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var providerMessageId = Guid.NewGuid().ToString("N");

        var recipient = request.Channel switch
        {
            NotificationChannel.Email => request.RecipientEmail,
            NotificationChannel.Sms => request.RecipientPhone,
            _ => request.RecipientEmail ?? request.RecipientPhone
        };

        var bodyPreview = string.IsNullOrEmpty(request.Body)
            ? string.Empty
            : (request.Body.Length <= BodySnippetLength
                ? request.Body
                : request.Body[..BodySnippetLength] + "...");

        _logger.LogInformation(
            "[Notification:{Provider}] Type={Type} Channel={Channel} Recipient={Recipient} Subject={Subject} ProviderMessageId={MessageId} BodyPreview={BodyPreview}",
            ProviderName,
            request.Type,
            request.Channel,
            recipient,
            request.Subject,
            providerMessageId,
            bodyPreview);

        return Task.FromResult(NotificationSendResult.Succeeded(ProviderName, providerMessageId));
    }
}

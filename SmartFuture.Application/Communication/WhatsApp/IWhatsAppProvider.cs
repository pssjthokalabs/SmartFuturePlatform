using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Communication.WhatsApp;

/// <summary>
/// Low-level send contract for WhatsApp messages. Same shape as
/// <see cref="Sms.ISmsProvider"/> — Twilio's WhatsApp API is the
/// expected first implementation but the interface stays
/// provider-agnostic.
/// </summary>
public interface IWhatsAppProvider
{
    Task<Result<WhatsAppSendOutcome>> SendAsync(WhatsAppSendRequest request, CancellationToken cancellationToken = default);
}

public sealed record WhatsAppSendRequest(string ToPhoneNumber, string Body, string? TemplateName = null, string? CorrelationId = null);

public sealed record WhatsAppSendOutcome(string ProviderName, string ProviderMessageId);

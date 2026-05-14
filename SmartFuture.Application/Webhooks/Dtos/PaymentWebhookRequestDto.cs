using SmartFuture.Shared.Enums.Webhooks;

namespace SmartFuture.Application.Webhooks.Dtos;

public class PaymentWebhookRequestDto
{
    public WebhookProvider Provider { get; set; } = WebhookProvider.Unknown;
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderEventId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string RawPayload { get; set; } = string.Empty;
    public string? SignatureHeader { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

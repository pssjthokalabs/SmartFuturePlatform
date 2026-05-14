using SmartFuture.Shared.Enums.Webhooks;

namespace SmartFuture.Application.Webhooks.Dtos;

public class ParsedPaymentWebhookDto
{
    public WebhookEventType EventType { get; set; } = WebhookEventType.Unknown;
    public string? ProviderEventId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? PaymentReference { get; set; }
    public string? InvoiceReference { get; set; }
    public string? OrderReference { get; set; }
    public string? GatewayTransactionId { get; set; }
    public decimal? Amount { get; set; }
    public string? CurrencyCode { get; set; }
    public DateTime? ProviderCreatedAtUtc { get; set; }
    public string? ParsedSummaryJson { get; set; }
}

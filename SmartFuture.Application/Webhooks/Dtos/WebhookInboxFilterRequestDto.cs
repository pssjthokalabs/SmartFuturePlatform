using SmartFuture.Shared.Enums.Webhooks;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Webhooks.Dtos;

public class WebhookInboxFilterRequestDto : PagedListQueryBase
{
    public WebhookProvider? Provider { get; set; }
    public string? ProviderName { get; set; }
    public WebhookInboxStatus? StatusFilter { get; set; }
    public WebhookEventType? EventType { get; set; }

    public string? ProviderEventId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? PaymentReference { get; set; }
    public string? InvoiceReference { get; set; }
    public string? OrderReference { get; set; }
    public string? GatewayTransactionId { get; set; }

    public Guid? PaymentId { get; set; }
    public Guid? InvoiceId { get; set; }

    public DateTime? ProcessedFromUtc { get; set; }
    public DateTime? ProcessedToUtc { get; set; }
}

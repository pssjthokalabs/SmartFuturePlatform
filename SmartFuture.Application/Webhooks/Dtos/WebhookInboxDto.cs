using SmartFuture.Shared.Enums.Webhooks;

namespace SmartFuture.Application.Webhooks.Dtos;

public class WebhookInboxDto
{
    public Guid Id { get; set; }
    public WebhookProvider Provider { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderEventId { get; set; }
    public WebhookEventType EventType { get; set; }
    public WebhookInboxStatus Status { get; set; }

    public string? RawPayloadHash { get; set; }
    public string? IdempotencyKey { get; set; }

    public string? PaymentReference { get; set; }
    public string? InvoiceReference { get; set; }
    public string? OrderReference { get; set; }
    public string? GatewayTransactionId { get; set; }

    public decimal? Amount { get; set; }
    public string? CurrencyCode { get; set; }

    public DateTime? ProviderCreatedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }

    public string? FailureReason { get; set; }
    public string? ParsedSummaryJson { get; set; }

    public Guid? PaymentId { get; set; }
    public Guid? InvoiceId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

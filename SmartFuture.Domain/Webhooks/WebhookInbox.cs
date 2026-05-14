using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Webhooks;

namespace SmartFuture.Domain.Webhooks;

public class WebhookInbox : BaseEntity
{
    public WebhookProvider Provider { get; set; } = WebhookProvider.Unknown;
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderEventId { get; set; }

    public WebhookEventType EventType { get; set; } = WebhookEventType.Unknown;
    public WebhookInboxStatus Status { get; set; } = WebhookInboxStatus.Received;

    public string? RawPayloadHash { get; set; }
    public string? SignatureHeader { get; set; }
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
    public Payment? Payment { get; set; }

    public Guid? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
}

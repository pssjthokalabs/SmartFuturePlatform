using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.BillingOps.Dtos;

/// <summary>A charge attempt (one PaymentInitiation). Non-sensitive only — no token / authorization code / raw gateway payload.</summary>
public sealed class ChargeRowDto
{
    public Guid InitiationId { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public PaymentProviderType Provider { get; set; }
    public PaymentInitiationStatus Status { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? WebhookLastReceivedAtUtc { get; set; }
    /// <summary>Short internal reason only (never a raw gateway payload).</summary>
    public string? FailureReason { get; set; }
}

/// <summary>A retry attempt (one PaymentRetryAttempt). Non-sensitive only.</summary>
public sealed class RetryRowDto
{
    public Guid AttemptId { get; set; }
    public int AttemptNumber { get; set; }
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public PaymentProviderType Provider { get; set; }
    public PaymentRetryAttemptStatus Status { get; set; }
    public decimal Amount { get; set; }
    public DateTime ScheduledForUtc { get; set; }
    public DateTime? AttemptedUtc { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>A billing notification row (OutboundNotification). Email is shown for admin ops; no payment secrets are ever stored here.</summary>
public sealed class NotificationRowDto
{
    public Guid Id { get; set; }
    public NotificationType Type { get; set; }
    public NotificationStatus Status { get; set; }
    public string? RecipientEmail { get; set; }
    public string? Subject { get; set; }
    public string? RelatedEntityType { get; set; }
    public Guid? RelatedEntityId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>An invoice attributed to a run by time window (approximate).</summary>
public sealed class GeneratedInvoiceRowDto
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public Guid? ScheduleId { get; set; }
    public InvoiceStatus Status { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal BalanceDue { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";
    public DateTime? PeriodStartUtc { get; set; }
    public DateTime? PeriodEndUtc { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime? IssuedAtUtc { get; set; }
}

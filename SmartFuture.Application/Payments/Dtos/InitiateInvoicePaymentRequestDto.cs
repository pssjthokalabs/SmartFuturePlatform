using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Dtos;

public class InitiateInvoicePaymentRequestDto
{
    public Guid InvoiceId { get; set; }
    public PaymentProviderType Provider { get; set; } = PaymentProviderType.Manual;
    public decimal? Amount { get; set; }

    public string? SuccessUrl { get; set; }
    public string? CancelUrl { get; set; }
    public string? FailureUrl { get; set; }

    // Phase 1 — admin-only dry-run flag. When true, the resulting
    // PaymentInitiation is created with WebhookApplyMode=ValidateOnly,
    // so the eventual webhook validates but does NOT mark the invoice
    // paid. The gateway service hard-rejects this flag unless the
    // caller is admin AND the host is non-production.
    public bool? SuppressWebhookApplication { get; set; }
}

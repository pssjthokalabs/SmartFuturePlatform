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

    /// <summary>
    /// Hybrid PayFast choice. When true the customer chose "Auto-renewal"
    /// and consents to tokenization — the PayFast checkout requests
    /// <c>subscription_type=2</c> so future renewals can be auto-charged.
    /// When false / omitted (the fail-safe default) it is a once-off
    /// payment and NO subscription_type is sent. Only honoured for the
    /// PayFast provider; ignored by Paystack/Ozow/Manual.
    /// </summary>
    public bool SaveForAutoRenewal { get; set; }
}

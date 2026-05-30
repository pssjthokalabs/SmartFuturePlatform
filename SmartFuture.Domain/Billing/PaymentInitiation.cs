using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Domain.Billing;

public class PaymentInitiation : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public Guid? PaymentId { get; set; }
    public Payment? Payment { get; set; }

    public PaymentProviderType Provider { get; set; } = PaymentProviderType.Manual;
    public PaymentInitiationStatus Status { get; set; } = PaymentInitiationStatus.Created;

    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";

    public string? ProviderReference { get; set; }
    public string? ProviderCheckoutId { get; set; }
    public string? RedirectUrl { get; set; }
    public string? SuccessUrl { get; set; }
    public string? CancelUrl { get; set; }
    public string? FailureUrl { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public string? MetadataJson { get; set; }

    // Phase 1 — per-initiation switch for the inbound webhook handler.
    // When ValidateOnly, the handler still runs signature / amount /
    // currency / reference checks (and logs the result) but does NOT
    // call IPaymentApplierService.ApplyStatusChangeAsync — the invoice
    // stays unpaid. Used for early Paystack UAT testing in isolation
    // from the rest of the invoice/order lifecycle. The flag is
    // authoritative; Paystack metadata is informational only.
    public WebhookApplyMode WebhookApplyMode { get; set; } = WebhookApplyMode.ApplyNormally;

    // Last time the webhook for this initiation was received +
    // validated. Set on every receipt (including dry-run paths) so
    // admin tools can show "webhook landed at X" without grepping
    // the inbox.
    public DateTime? WebhookLastReceivedAtUtc { get; set; }
}

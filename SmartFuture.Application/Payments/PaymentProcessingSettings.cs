namespace SmartFuture.Application.Payments;

/// <summary>
/// Phase 1 — coarse-grained kill switch for the payment webhook
/// applier. When <see cref="WebhookApplyEnabled"/> is false, EVERY
/// payment webhook (Paystack, PayFast, Ozow) short-circuits before
/// the apply step: signature is still verified, the receipt is still
/// logged, but no invoice flips to Paid. Per-initiation control still
/// lives on <c>PaymentInitiation.WebhookApplyMode</c> — this flag is
/// the higher-priority master switch.
///
/// Bound from the <c>PaymentProcessing</c> config section. Defaults
/// to <c>true</c> so existing production deployments keep behaving
/// exactly the same.
/// </summary>
public class PaymentProcessingSettings
{
    public const string SectionName = "PaymentProcessing";

    public bool WebhookApplyEnabled { get; set; } = true;
}

namespace SmartFuture.Shared.Enums.Payments;

// Phase 1 of the Paystack recurring-billing rollout. Per-initiation
// flag the webhook handler consults before applying a status change.
//
// The decision MUST come from our own PaymentInitiation row, never
// from anything in the inbound webhook body — Paystack's metadata is
// echoed-back data and not trustworthy as a control signal.
//
//   ApplyNormally   — production default. Validated webhook flips
//                     the payment to Completed via the applier.
//   ValidateOnly    — UAT/dry-run. Webhook is still signature-checked,
//                     reference-matched, amount/currency-validated,
//                     and audited — but PaymentApplierService is NOT
//                     called and the invoice is NOT marked paid.
public enum WebhookApplyMode
{
    ApplyNormally = 0,
    ValidateOnly = 1
}

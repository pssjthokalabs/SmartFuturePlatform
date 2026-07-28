namespace SmartFuture.Application.Payments.Ozow;

/// <summary>
/// Ozow equivalent of <see cref="PayFast.IPayFastIntentInitiationService"/> /
/// <see cref="Paystack.IPaystackIntentInitiationService"/>.
///
/// Initiates an Ozow transaction tied to an OrderIntent — WITHOUT
/// creating a real Order / Invoice / Payment up-front. Those rows are
/// materialised later by
/// <c>OrderIntentService.ConvertIntentPaymentToPaidOrderAsync</c>, called
/// from <see cref="OzowNotifyHandler"/> when Ozow's server-to-server
/// notification confirms a Complete payment.
///
/// Unlike PayFast (a signed form-redirect built offline) Ozow requires a
/// real outbound HTTP POST to <c>PostPaymentRequest</c>, which returns the
/// hosted-checkout URL. That transport is shared with the invoice flow via
/// <c>OzowRequestSender</c> so both sign identically.
///
/// The reference pattern matches the Paystack/PayFast intent flows
/// (<c>SF-INTENT-{12-hex}</c>) and is sent verbatim as Ozow's
/// <c>TransactionReference</c>, so a single prefix check on the inbound
/// notification distinguishes an intent payment from an invoice payment.
/// At 22 characters it is comfortably inside Ozow's 50-char cap.
///
/// SAFETY NOTE — Ozow order-intents settle by webhook ONLY. There is no
/// client-side verify step (Paystack has one; Ozow does not). If the
/// notification never arrives, the customer has been charged and no Order
/// exists. Everything this service persists onto the intent
/// (provider reference, Ozow paymentRequestId, amount sent) exists so that
/// case is reconcilable rather than invisible — see the
/// <c>[OzowIntentReconcile]</c> log tag and the unconverted-intent query
/// in <c>OzowNotifyHandler</c>.
///
/// Implementation lives in
/// <c>SmartFuture.Infrastructure.Payments.Ozow.OzowIntentInitiationService</c>.
/// </summary>
public interface IOzowIntentInitiationService
{
    Task<OzowIntentInitiationResult> InitiateAsync(
        OzowIntentInitiationRequest request,
        CancellationToken cancellationToken = default);
}

public class OzowIntentInitiationRequest
{
    public Guid OrderIntentId { get; set; }

    /// <summary>Pre-minted SF-INTENT-… reference. Sent to Ozow verbatim as
    /// TransactionReference and written to
    /// <c>OrderIntent.IntentPaymentReference</c>.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>The real amount owed (installation fee). The test-amount
    /// override, when active, replaces what is SENT — this value is kept
    /// for the audit trail.</summary>
    public decimal InvoiceAmountAtTime { get; set; }

    /// <summary>Shown on the customer's bank statement (max 20 chars,
    /// truncated by the service). Defaults to "SmartFuture".</summary>
    public string? BankReferenceLabel { get; set; }

    public string? SuccessUrlOverride { get; set; }
    public string? CancelUrlOverride { get; set; }
    public string? ErrorUrlOverride { get; set; }
}

public class OzowIntentInitiationResult
{
    public bool Success { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>SF-INTENT-… reference echoed back by Ozow as
    /// TransactionReference on the notification.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>Ozow hosted-checkout URL. Caller hands this to the portal
    /// for a same-tab redirect.</summary>
    public string? RedirectUrl { get; set; }

    /// <summary>Ozow's own id for this checkout session. Persisted on the
    /// intent so a support agent can look the transaction up in the Ozow
    /// dashboard when a notification goes missing.</summary>
    public string? PaymentRequestId { get; set; }

    public decimal AmountSent { get; set; }
    public decimal InvoiceAmountAtTime { get; set; }
    public bool IsTestAmountOverrideApplied { get; set; }
    public string? Currency { get; set; }

    /// <summary>True when Ozow was in test mode at initiate. Echoed onto
    /// the intent for diagnostics.</summary>
    public bool IsTest { get; set; }

    public static OzowIntentInitiationResult Fail(string reason) =>
        new() { Success = false, FailureReason = reason };
}

namespace SmartFuture.Application.Payments.PayFast;

/// <summary>
/// PayFast equivalent of <see cref="Paystack.IPaystackIntentInitiationService"/>.
///
/// Initiates a PayFast transaction tied to an OrderIntent — WITHOUT
/// creating a real Order / Invoice / Payment up-front. The Order +
/// Invoice + Payment rows are materialised later by
/// <c>OrderIntentService.ConvertIntentPaymentToPaidOrderAsync</c>, called
/// from <see cref="PayFastNotifyHandler"/> when PayFast's server-to-server
/// ITN confirms a successful payment.
///
/// Unlike the Paystack initiator there is NO outbound HTTP call —
/// PayFast hosted-checkout is a signed form-redirect, so the service
/// just builds the signed parameter list and returns the redirect URL.
/// The reference pattern matches the Paystack intent flow
/// (<c>SF-INTENT-{12-hex}</c>) so a single PayFast `m_payment_id` lookup
/// in <c>OrderIntents</c> finds the intent regardless of provider.
///
/// Implementation lives in
/// <c>SmartFuture.Infrastructure.Payments.PayFast.PayFastIntentInitiationService</c>.
/// </summary>
public interface IPayFastIntentInitiationService
{
    Task<PayFastIntentInitiationResult> InitiateAsync(
        PayFastIntentInitiationRequest request,
        CancellationToken cancellationToken = default);
}

public class PayFastIntentInitiationRequest
{
    public Guid OrderIntentId { get; set; }
    public string CustomerEmail { get; set; } = string.Empty;
    public string? CustomerFirstName { get; set; }
    public string? CustomerLastName { get; set; }
    public decimal InvoiceAmountAtTime { get; set; }
    /// <summary>Display label for `item_name`. Defaults to the
    /// package name when empty.</summary>
    public string? ItemName { get; set; }
    public string? ReturnUrlOverride { get; set; }
    public string? CancelUrlOverride { get; set; }

    /// <summary>
    /// True when the customer chose PayFast "Auto-renewal" (consent to
    /// tokenization). Drives whether <c>subscription_type=2</c> is sent.
    /// Default false = once-off.
    /// </summary>
    public bool SaveForAutoRenewal { get; set; }
}

public class PayFastIntentInitiationResult
{
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>SF-INTENT-… reference written to the OrderIntent +
    /// posted back by PayFast as `m_payment_id` on ITN.</summary>
    public string Reference { get; set; } = string.Empty;
    /// <summary>PayFast hosted-checkout redirect URL (signed query
    /// string). Caller hands this to the mobile/portal client.</summary>
    public string? RedirectUrl { get; set; }
    public decimal AmountSent { get; set; }
    public decimal InvoiceAmountAtTime { get; set; }
    public bool IsTestAmountOverrideApplied { get; set; }
    public string? Currency { get; set; }
    /// <summary>True when PayFast was running against the sandbox host
    /// at the time of initiate. Echoed onto the intent for diagnostics.</summary>
    public bool IsSandbox { get; set; }

    public static PayFastIntentInitiationResult Fail(string reason) =>
        new() { Success = false, FailureReason = reason };
}

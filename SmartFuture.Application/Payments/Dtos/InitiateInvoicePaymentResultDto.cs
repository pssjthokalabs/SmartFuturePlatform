using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Dtos;

public class InitiateInvoicePaymentResultDto
{
    public bool Success { get; set; }
    public PaymentProviderType Provider { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? PaymentInitiationId { get; set; }
    public string? PaymentNumber { get; set; }
    public string? ProviderReference { get; set; }
    public string? RedirectUrl { get; set; }
    public string? FailureReason { get; set; }

    // Phase 53.3 — debug fields propagated from the provider.
    // Surfaced to the mobile + portal client so the user (or
    // on-call engineer) sees the specific reason a payment failed
    // without grepping logs. SAFE TO RETURN — no secrets.
    public int?    ProviderStatusCode       { get; set; }
    public string? ProviderErrorMessage     { get; set; }
    public string? ProviderEndpoint         { get; set; }
    public bool?   ProviderIsTest           { get; set; }
    public string? ProviderRawResponseSnippet { get; set; }

    // ─── Inline / embedded checkout ─────────────────────────────────
    // Populated only for providers that support an embedded cashier
    // flow (currently Paystack). The portal uses these fields to open
    // Paystack's InlineJS overlay on top of the invoice page — no full
    // redirect, no popup window, no iframe. SAFE TO RETURN: public
    // key, access code, customer email, amount-in-subunits, currency,
    // reference. The Paystack secret key NEVER leaves the API tier.
    //
    // Null for providers that don't support inline (PayFast, Ozow) —
    // the portal falls back to RedirectUrl for those.
    public PaystackInlineCheckoutDto? PaystackInline { get; set; }
}

public class PaystackInlineCheckoutDto
{
    /// <summary>Paystack public key (pk_test_… / pk_live_…). Safe to return; never log secret key.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Access code returned by Paystack's transaction/initialize call.
    /// Preferred path: portal opens the inline popup via
    /// <c>resumeTransaction({accessCode})</c> so the existing
    /// backend-initialised transaction is reused — never recreate
    /// from the frontend.
    /// </summary>
    public string? AccessCode { get; set; }

    /// <summary>Server-resolved customer email (from invoice → order → user). Mirrors what was sent to Paystack.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Amount in the smallest currency subunit (ZAR cents). Mirrors what was sent to Paystack.</summary>
    public long AmountSubunits { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = "ZAR";

    /// <summary>SmartFuture-side reference (SF-PAY-…). Same value the webhook will quote.</summary>
    public string Reference { get; set; } = string.Empty;
}

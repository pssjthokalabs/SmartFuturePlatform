using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.OrderIntents.Dtos;

// Phase 53 — "Order and Pay" client checkout shapes. Lives alongside
// the existing OrderIntent DTOs so callers in the same namespace
// resolve cleanly. The request carries the package + coverage-confirmed
// address + contact info the customer just provided on the portal
// /client/orders/new page; the response carries everything the portal
// needs to launch the Paystack inline overlay immediately.

public class InitiateOrderIntentPaymentRequestDto
{
    public Guid ServicePackageId { get; set; }

    // Optional selected variant. When set + active for the package, its
    // price/activation-fee/free-flag drive the checkout breakdown + the
    // Order snapshot on conversion. Null → the package's own pricing.
    public Guid? ServicePackageVariantId { get; set; }

    // Which gateway to initiate against. Optional; the service defaults
    // to Paystack when this is null/Unknown so the existing portal
    // callers stay byte-identical. Mobile callers pass `Paystack` or
    // `PayFast` explicitly; the ClientZone new-order page can also pass
    // `Ozow`.
    //
    // Supported: Paystack (3), PayFast (1), Ozow (5) — each has a
    // dedicated intent-initiation service. Anything else
    // (PeachPayments / Yoco / Manual) is rejected up-front.
    //
    // Ozow additionally requires Ozow__Enabled=true on the API tier; when
    // it is false the service returns PROVIDER_NOT_CONFIGURED with a
    // friendly message rather than reaching the gateway.
    public PaymentProviderType? Provider { get; set; }

    // Customer contact (server still reads identity for trust; these
    // are used only for the gateway initialise / signed-redirect payload).
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    // Coverage-confirmed address. Latitude/Longitude are REQUIRED on
    // the customer path — the service rejects free-text-only intents
    // the same way OrderService.CreateMineAsync does today.
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? GooglePlaceId { get; set; }
    public string? MapProviderReference { get; set; }

    // Residence/property type + conditional building/unit info. See
    // Order.PropertyType remarks. Required (service-layer) for a new
    // Fibre intent; optional otherwise.
    public PropertyType? PropertyType { get; set; }
    public string? BuildingComplexName { get; set; }
    public string? UnitNumber { get; set; }

    // Fibre: the customer's confirmed choice of Openserve service location
    // (CoverageCheckResponseDto.ServicePremisesReference) when no Openserve
    // record matched their address automatically. Re-validated at checkout;
    // ignored when a record matches. Their installation address (above) is
    // never replaced by it. Omitted by callers that predate it.
    public Guid? OpenserveServicePremisesReference { get; set; }

    public DateTime? RequestedInstallationDateUtc { get; set; }
    public string? CustomerNotes { get; set; }

    // Optional return URLs for the Paystack hosted-redirect fallback.
    public string? SuccessUrl { get; set; }
    public string? CancelUrl { get; set; }

    /// <summary>
    /// Hybrid PayFast choice for the order-intent / first-service payment.
    /// True = "Auto-renewal" (consent to tokenization; PayFast checkout
    /// requests <c>subscription_type=2</c>). False / omitted (fail-safe
    /// default) = once-off, no subscription_type. PayFast-only; ignored
    /// by Paystack.
    /// </summary>
    public bool SaveForAutoRenewal { get; set; }

    /// <summary>
    /// Day-of-month for the customer's monthly billing cycle. Must be
    /// one of the currently-enabled BillingDayOption values (typically
    /// 15/25/30). Nullable for backwards compatibility: legacy mobile
    /// clients that don't yet expose the picker fall back to the
    /// configured default (30) on the server. The website + ClientZone
    /// checkout always send a value.
    /// </summary>
    public int? PreferredBillingDay { get; set; }
}

public class InitiateOrderIntentPaymentResponseDto
{
    public Guid OrderIntentId { get; set; }
    public string IntentToken { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string Provider { get; set; } = "Paystack";

    // Mirrors the existing PaymentInitiation response shape so the
    // portal's Paystack inline UX can be reused 1:1.
    public string? RedirectUrl { get; set; }
    public PaystackInlineCheckoutDto? PaystackInline { get; set; }

    // Audit echo — full installation fee + actual amount sent to
    // Paystack (in UAT they differ when the live-amount override is
    // active). The portal renders InvoiceAmount, not AmountSent.
    public decimal InvoiceAmount { get; set; }
    public decimal AmountSent { get; set; }
    public bool IsTestAmountOverrideApplied { get; set; }

    // Line-item breakdown of the InvoiceAmount so the portal + mobile
    // can render "Activation R2500 + Pro-rata R549.63". ProRata is 0
    // for Fibre (service fee starts after activation) and non-zero for
    // Security when the customer joins before their billing day.
    public decimal ActivationFeeAmount { get; set; }
    public decimal ProRataAmount { get; set; }
    public int ProRataDays { get; set; }
    public DateTime? ProRataPeriodStartUtc { get; set; }
    public DateTime? ProRataPeriodEndUtc { get; set; }
    public int PreferredBillingDay { get; set; }

    /// <summary>
    /// Safe diagnostics block for operator verification ("is the API
    /// actually reading PayFast__MerchantId from my env vars?"). ONLY
    /// populated in non-Production environments — null in Production
    /// regardless of caller. NEVER contains the merchant key,
    /// passphrase, secret key, signature, or any other credential.
    /// </summary>
    public InitiateOrderIntentDebugDto? Debug { get; set; }
}

/// <summary>
/// Non-secret diagnostics returned only in non-Production. Mobile and
/// Portal log this so the operator can confirm the API is sending the
/// right merchant id / sandbox flag / callback hosts to the gateway.
/// </summary>
public class InitiateOrderIntentDebugDto
{
    public string? Provider { get; set; }
    public string? Environment { get; set; }
    public bool? UseSandbox { get; set; }
    /// <summary>The MerchantId as configured on the API (PayFast).
    /// Verbatim — operators need to compare it against the PayFast
    /// dashboard. Empty when the gateway doesn't use a merchant id
    /// (Paystack uses SecretKey instead). Source name is also echoed
    /// so the operator knows which env var the value came from.</summary>
    public string? MerchantId { get; set; }
    public string? MerchantIdSource { get; set; }
    public string? PayFastHost { get; set; }
    public string? NotifyUrl { get; set; }
    public string? ReturnUrl { get; set; }
    public string? CancelUrl { get; set; }

    // Test-amount override audit. Surfaced so the operator can
    // confirm via the Expo log that the API used the override (e.g.
    // forcing R5 against a LIVE PayFast merchant for end-to-end
    // testing) and what the original invoice amount was.
    public bool? UseTestAmountOverride { get; set; }
    public decimal? TestAmount { get; set; }
    /// <summary>Original installation-fee amount the customer would
    /// pay if the override were off. NEVER overwritten in the DB —
    /// invoice rows stay at this value.</summary>
    public decimal? OriginalAmount { get; set; }
    /// <summary>The amount the API actually sent to PayFast in the
    /// `amount` form field. Equals <see cref="TestAmount"/> when the
    /// override fired, otherwise equals <see cref="OriginalAmount"/>.</summary>
    public decimal? EffectiveAmount { get; set; }
}

public class ConvertIntentPaymentToPaidOrderOutcomeDto
{
    public string Reference { get; set; } = string.Empty;
    public Guid OrderIntentId { get; set; }
    public bool AlreadyConverted { get; set; }

    public Guid OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;

    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;

    public Guid PaymentId { get; set; }
    public string PaymentNumber { get; set; } = string.Empty;

    public Guid? NetworkAccountId { get; set; }

    public decimal InvoiceAmount { get; set; }
    public decimal ProviderAmount { get; set; }
    public bool OverrideApplied { get; set; }
}

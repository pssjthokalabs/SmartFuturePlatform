using SmartFuture.Application.Payments.Dtos;
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

    // Which gateway to initiate against. Optional; the service
    // defaults to Paystack when this is null/Unknown so the existing
    // portal callers stay byte-identical. Mobile callers pass
    // `Paystack` or `PayFast` explicitly. Backend rejects any
    // provider other than Paystack/PayFast — Ozow/Yoco/etc. are not
    // wired into the intent flow.
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

    public DateTime? RequestedInstallationDateUtc { get; set; }
    public string? CustomerNotes { get; set; }

    // Optional return URLs for the Paystack hosted-redirect fallback.
    public string? SuccessUrl { get; set; }
    public string? CancelUrl { get; set; }
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

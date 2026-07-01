using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Domain.OrderIntents;

// Public pre-order intent. Created anonymously by the marketing site
// when a visitor clicks "Get Started" on a fibre package, captures the
// fields needed to seed a real order, and is later "claimed" + "converted"
// by an authenticated client in the portal. The intent NEVER becomes the
// order itself — see OrderIntentService.ConvertAsync, which calls the
// existing IOrderService.CreateMineAsync so backend pricing/snapshots
// remain authoritative.
public class OrderIntent : BaseEntity
{
    public string IntentToken { get; set; } = string.Empty;

    public Guid ServicePackageId { get; set; }
    public ServicePackage? ServicePackage { get; set; }

    // Selected package variant captured at checkout so the paid-webhook /
    // recovery conversion path can re-resolve the variant's effective
    // pricing when it materialises the Order days later. Null = no variant
    // (variant-less package or a legacy intent).
    public Guid? ServicePackageVariantId { get; set; }
    public ServicePackageVariant? ServicePackageVariant { get; set; }

    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    public string? AddressLine1 { get; set; }
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

    public OrderIntentStatus Status { get; set; } = OrderIntentStatus.Pending;

    public Guid? ClaimedByUserId { get; set; }
    public User? ClaimedByUser { get; set; }

    public Guid? ConvertedOrderId { get; set; }
    public Order? ConvertedOrder { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public DateTime? ConvertedAtUtc { get; set; }

    public string? Source { get; set; }

    // Phase 9 — legal consent capture. The website's pre-order wizard
    // requires the visitor to tick a single "I agree to the Terms /
    // acknowledge the Privacy Policy" checkbox before submit; the
    // backend records the consent here so we have a server-side record
    // tied to the intent (and, by extension, the user that the intent
    // is claimed by). Version strings are short YYYY-MM tags shared
    // between the website's legalVersions.js and the rendered pages.
    public string? TermsVersion { get; set; }
    public DateTime? TermsAcceptedAtUtc { get; set; }
    public string? PrivacyVersion { get; set; }
    public DateTime? PrivacyAcknowledgedAtUtc { get; set; }

    // Phase 53 — Order-first-then-pay rewrite. When the customer clicks
    // "Order and Pay" on the portal, we DO NOT create a real Order
    // yet — we create an OrderIntent and initiate a Paystack
    // transaction against the intent. These fields track that
    // Paystack initiation so the webhook + verify-and-apply paths can
    // look the intent up by ProviderReference (uniquely indexed).
    //
    // After Paystack confirms success, OrderIntentService.
    // ConvertIntentToPaidOrderAsync atomically creates Order +
    // Invoice (Paid) + Payment (Completed) + Pending NetworkAccount,
    // marks the intent ConvertedToOrder, and links ConvertedOrderId.
    public string? IntentPaymentReference { get; set; }
    public string? IntentPaymentAccessCode { get; set; }
    public string? IntentPaymentRedirectUrl { get; set; }
    public decimal? IntentPaymentAmount { get; set; }
    public decimal? IntentInvoiceAmountAtTime { get; set; }
    public bool IntentIsTestAmountOverrideApplied { get; set; }
    public DateTime? IntentPaymentInitiatedAtUtc { get; set; }

    // Which gateway minted the IntentPaymentReference / RedirectUrl
    // above. Was hardcoded Paystack pre-PayFast; now persisted so the
    // notify handler + ConvertIntentPaymentToPaidOrderAsync can stamp
    // the materialised Payment/PaymentInitiation with the right
    // gateway name. Default Paystack keeps the portal's existing
    // intent flow byte-identical until the request explicitly opts
    // into PayFast.
    public PaymentProviderType Provider { get; set; } = PaymentProviderType.Paystack;

    // Customer-selectable billing day captured during checkout. Passed
    // through to Order.PreferredBillingDay when the intent is converted.
    // Persisted on the intent (rather than only on the order) so the
    // intent-recovery / paid-webhook path can materialise the order
    // days later using the same billing preference the customer picked.
    // Null = fall back to the default at conversion time (BillingSettings
    // + BillingDayOption default row); the migration backfills existing
    // rows to null so nothing breaks.
    public int? PreferredBillingDay { get; set; }
}

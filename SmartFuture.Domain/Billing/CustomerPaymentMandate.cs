using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Domain.Billing;

// Reusable payment authorization captured from a successful gateway
// charge. For Paystack today, this is the authorization object
// returned in `data.authorization` of a charge.success event when
// `reusable=true`. Lets the auto-debit job (Phase 5/6) call
// `POST /transaction/charge_authorization` for monthly service fees
// without ever holding PAN/CVV — Paystack stores those on their side.
//
// CARDHOLDER DATA RULE
// --------------------
// Only fields documented as safe-to-store by the provider live here:
// the authorization code (encrypted at rest), last-4 of the PAN, exp
// month/year, card type, bank, and an account holder name when
// supplied. Full PAN, CVV, and 3DS data NEVER touch this entity.
public class CustomerPaymentMandate : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Gateway that issued the authorization.</summary>
    public PaymentProviderType Provider { get; set; } = PaymentProviderType.Paystack;

    /// <summary>Paystack's per-customer reference if available.</summary>
    public string? ProviderCustomerCode { get; set; }

    /// <summary>
    /// **Sensitive.** Paystack authorization code — encrypted at rest
    /// via ASP.NET Core DataProtection (purpose: "CustomerPaymentMandate.AuthorizationCode").
    /// Only ever read inside <c>PaystackChargeAuthorizationService</c>
    /// and never logged or returned to the client. Length is generous
    /// to absorb future provider format changes.
    /// </summary>
    public string AuthorizationCodeProtected { get; set; } = string.Empty;

    /// <summary>
    /// Paystack's authorization signature. Stable per-card hash —
    /// used as the natural-key for dedupe ("is this the same card?").
    /// Not a secret on its own but treat as opaque.
    /// </summary>
    public string? AuthorizationSignature { get; set; }

    public string? Channel { get; set; }
    public string? CardType { get; set; }
    public string? Bank { get; set; }
    public string? Last4 { get; set; }
    public string? ExpMonth { get; set; }
    public string? ExpYear { get; set; }
    public string? AccountName { get; set; }

    /// <summary>Email Paystack has on file for this authorization.</summary>
    public string? CustomerEmail { get; set; }

    /// <summary>True when the gateway told us this authorization can be re-charged.</summary>
    public bool IsReusable { get; set; }

    /// <summary>
    /// False once the customer revokes or the mandate has been hard-deactivated
    /// (e.g. consecutive failures threshold reached). Auto-debit job skips
    /// inactive mandates.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// One default per (UserId, Provider). Auto-debit job picks the default.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTime? ConsentGivenUtc { get; set; }
    public DateTime? ConsentRevokedUtc { get; set; }
    public CustomerMandateConsentSource ConsentSource { get; set; } = CustomerMandateConsentSource.InstallationCheckout;

    public DateTime? LastSuccessfulChargeUtc { get; set; }
    public DateTime? LastFailedChargeUtc { get; set; }
    public int ConsecutiveFailureCount { get; set; }

    /// <summary>Free-form provider metadata snapshot — non-sensitive only.</summary>
    public string? MetadataJson { get; set; }
}

using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.Mandates;

/// <summary>
/// Internal-only DTO the PayFast ITN handler passes to
/// <c>ICustomerPaymentMandateService.UpsertPayFastMandateAsync</c> after a
/// successful tokenization-setup payment. Never exposed via REST.
///
/// Carries the RAW PayFast token — the service encrypts it before storing
/// (in the existing protected field) and derives the dedupe signature as a
/// hash of the token. The token is NEVER logged or returned to the client.
/// </summary>
public class UpsertPayFastMandateRequestDto
{
    public Guid UserId { get; set; }

    /// <summary>Raw PayFast token from the ITN — service encrypts before store; never logged.</summary>
    public string Token { get; set; } = string.Empty;

    public string? CustomerEmail { get; set; }

    /// <summary>PayFast reference (m_payment_id / pf_payment_id) — stored as safe metadata only.</summary>
    public string? ProviderReference { get; set; }

    public CustomerMandateConsentSource ConsentSource { get; set; } = CustomerMandateConsentSource.InstallationCheckout;

    /// <summary>Optional safe metadata snapshot (non-sensitive only — never the token).</summary>
    public string? MetadataJson { get; set; }
}

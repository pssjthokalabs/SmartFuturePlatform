using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.Mandates;

/// <summary>
/// Internal-only DTO the Paystack webhook handler passes to
/// <c>ICustomerPaymentMandateService</c>. Never exposed via REST.
/// Carries the raw authorization fields straight from Paystack —
/// the service encrypts the authorization code before persisting.
/// </summary>
public class UpsertPaystackMandateRequestDto
{
    public Guid UserId { get; set; }

    /// <summary>Raw Paystack authorization_code — service encrypts before store.</summary>
    public string AuthorizationCode { get; set; } = string.Empty;

    public string? AuthorizationSignature { get; set; }
    public string? ProviderCustomerCode { get; set; }
    public string? Channel { get; set; }
    public string? CardType { get; set; }
    public string? Bank { get; set; }
    public string? Last4 { get; set; }
    public string? ExpMonth { get; set; }
    public string? ExpYear { get; set; }
    public string? AccountName { get; set; }
    public string? CustomerEmail { get; set; }
    public bool IsReusable { get; set; }

    public CustomerMandateConsentSource ConsentSource { get; set; } = CustomerMandateConsentSource.InstallationCheckout;
    public string? MetadataJson { get; set; }
}

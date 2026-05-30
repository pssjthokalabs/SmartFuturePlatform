using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Mandates;

/// <summary>
/// Customer-safe view of a stored payment authorization. Deliberately
/// excludes the encrypted authorization code and any field that could
/// be combined with last-4 to reveal a full PAN.
/// </summary>
public class CustomerPaymentMandateDto
{
    public Guid Id { get; set; }
    public PaymentProviderType Provider { get; set; }
    public string? Channel { get; set; }
    public string? CardType { get; set; }
    public string? Bank { get; set; }
    public string? Last4 { get; set; }
    public string? ExpMonth { get; set; }
    public string? ExpYear { get; set; }
    public string? AccountName { get; set; }
    public string? CustomerEmail { get; set; }

    public bool IsReusable { get; set; }
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }

    public DateTime? ConsentGivenUtc { get; set; }
    public DateTime? ConsentRevokedUtc { get; set; }
    public CustomerMandateConsentSource ConsentSource { get; set; }

    public DateTime? LastSuccessfulChargeUtc { get; set; }
    public DateTime? LastFailedChargeUtc { get; set; }
    public int ConsecutiveFailureCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Render-ready label, e.g. "Visa •••• 4081 (FNB)".</summary>
    public string DisplayLabel { get; set; } = string.Empty;
}

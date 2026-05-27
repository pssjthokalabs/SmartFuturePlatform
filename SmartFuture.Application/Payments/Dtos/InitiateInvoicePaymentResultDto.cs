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
    public int?    ProviderStatusCode   { get; set; }
    public string? ProviderErrorMessage { get; set; }
    public string? ProviderEndpoint     { get; set; }
    public bool?   ProviderIsTest       { get; set; }
}

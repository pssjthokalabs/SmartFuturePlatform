using SmartFuture.Application.Payments.Dtos;

namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Phase 53 — initiates a Paystack transaction for an OrderIntent,
/// without requiring a real Order / Invoice / Payment to exist yet.
/// Implementation lives in Infrastructure.
/// </summary>
public interface IPaystackIntentInitiationService
{
    Task<PaystackIntentInitiationResult> InitiateAsync(
        PaystackIntentInitiationRequest request,
        CancellationToken cancellationToken = default);
}

public class PaystackIntentInitiationRequest
{
    public Guid OrderIntentId { get; set; }
    public string CustomerEmail { get; set; } = string.Empty;
    public decimal InvoiceAmountAtTime { get; set; }
    public string? CallbackUrl { get; set; }
    public string? CancelUrl { get; set; }
}

public class PaystackIntentInitiationResult
{
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string? AccessCode { get; set; }
    public string? RedirectUrl { get; set; }
    public decimal AmountSent { get; set; }
    public decimal InvoiceAmountAtTime { get; set; }
    public bool IsTestAmountOverrideApplied { get; set; }
    public string? Currency { get; set; }
    public long AmountSubunits { get; set; }
    public string? PublicKey { get; set; }

    public PaystackInlineCheckoutDto? ToInlineDto(string customerEmail)
    {
        if (string.IsNullOrWhiteSpace(PublicKey)) return null;
        return new PaystackInlineCheckoutDto
        {
            PublicKey      = PublicKey,
            AccessCode     = AccessCode,
            Email          = customerEmail,
            AmountSubunits = AmountSubunits,
            Currency       = Currency ?? "ZAR",
            Reference      = Reference,
        };
    }

    public static PaystackIntentInitiationResult Fail(string reason) =>
        new() { Success = false, FailureReason = reason };
}

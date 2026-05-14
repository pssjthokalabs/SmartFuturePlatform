namespace SmartFuture.Application.Payments;

public class PaymentProviderInitiationResult
{
    public bool Success { get; set; }
    public string? ProviderReference { get; set; }
    public string? ProviderCheckoutId { get; set; }
    public string? RedirectUrl { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public string? MetadataJson { get; set; }

    public static PaymentProviderInitiationResult Succeeded(
        string? providerReference,
        string? providerCheckoutId = null,
        string? redirectUrl = null,
        DateTime? expiresAtUtc = null,
        string? metadataJson = null) => new()
    {
        Success = true,
        ProviderReference = providerReference,
        ProviderCheckoutId = providerCheckoutId,
        RedirectUrl = redirectUrl,
        ExpiresAtUtc = expiresAtUtc,
        MetadataJson = metadataJson
    };

    public static PaymentProviderInitiationResult FailedResult(string failureReason) => new()
    {
        Success = false,
        FailureReason = failureReason
    };
}

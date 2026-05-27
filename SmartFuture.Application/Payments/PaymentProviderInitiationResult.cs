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

    // Phase 53.3 — diagnostic fields propagated all the way to the
    // mobile client so the customer / on-call engineer can see WHY
    // an Ozow request failed without grepping CloudWatch.
    //   - ProviderStatusCode : HTTP status the gateway returned.
    //   - ProviderErrorMessage: gateway-supplied error text (e.g.
    //                           Ozow's "Invalid HashCheck").
    //   - ProviderEndpoint   : the URL we actually POSTed to.
    //   - ProviderIsTest     : the IsTest value sent in the request.
    // NEVER include secrets in these fields.
    public int?    ProviderStatusCode       { get; set; }
    public string? ProviderErrorMessage     { get; set; }
    public string? ProviderEndpoint         { get; set; }
    public bool?   ProviderIsTest           { get; set; }
    public string? ProviderRawResponseSnippet { get; set; }

    public static PaymentProviderInitiationResult Succeeded(
        string? providerReference,
        string? providerCheckoutId = null,
        string? redirectUrl = null,
        DateTime? expiresAtUtc = null,
        string? metadataJson = null,
        int? providerStatusCode = null,
        string? providerEndpoint = null,
        bool? providerIsTest = null,
        string? providerRawResponseSnippet = null) => new()
    {
        Success = true,
        ProviderReference = providerReference,
        ProviderCheckoutId = providerCheckoutId,
        RedirectUrl = redirectUrl,
        ExpiresAtUtc = expiresAtUtc,
        MetadataJson = metadataJson,
        ProviderStatusCode = providerStatusCode,
        ProviderEndpoint = providerEndpoint,
        ProviderIsTest = providerIsTest,
        ProviderRawResponseSnippet = providerRawResponseSnippet
    };

    public static PaymentProviderInitiationResult FailedResult(
        string failureReason,
        int? providerStatusCode = null,
        string? providerErrorMessage = null,
        string? providerEndpoint = null,
        bool? providerIsTest = null,
        string? providerReference = null,
        string? providerRawResponseSnippet = null) => new()
    {
        Success = false,
        FailureReason = failureReason,
        ProviderStatusCode = providerStatusCode,
        ProviderErrorMessage = providerErrorMessage,
        ProviderEndpoint = providerEndpoint,
        ProviderIsTest = providerIsTest,
        ProviderReference = providerReference,
        ProviderRawResponseSnippet = providerRawResponseSnippet
    };
}

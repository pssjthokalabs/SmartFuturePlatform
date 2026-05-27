using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Infrastructure.Payments.Ozow;

/// <summary>
/// Phase 52 — real Ozow PostPaymentRequest initiator.
///
/// Builds the Ozow request body server-side (the merchant private key
/// never leaves the API tier), POSTs it to Ozow, and returns the
/// `url` Ozow responds with so the customer can be redirected.
///
/// All hash + URL logic is in
/// <see cref="OzowHashCalculator"/> / <see cref="OzowSettings"/> —
/// this class owns only the HTTP transport + response mapping.
///
/// Coexists with the legacy mock-checkout flow: callers select Ozow
/// by passing <see cref="PaymentProviderType.Ozow"/>; mock-checkout
/// callers still hit the existing flow inside
/// <c>OrderService.PersistMockCheckoutAsync</c>.
/// </summary>
public class OzowPaymentInitiator : IPaymentInitiator
{
    // Phase 53.2 — staging is intentionally NOT auto-picked anymore.
    // We don't have staging-issued credentials right now, and using
    // live keys against staging silently fails ("merchant not found")
    // which is confusing. If we ever do have staging keys, set
    // Ozow:ApiUrl explicitly to the staging URL.
    private const string LiveApiUrl = "https://api.ozow.com/postpaymentrequest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly OzowSettings _settings;
    private readonly ILogger<OzowPaymentInitiator> _logger;

    public OzowPaymentInitiator(HttpClient httpClient, IOptions<OzowSettings> settings, ILogger<OzowPaymentInitiator> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public PaymentProviderType Provider => PaymentProviderType.Ozow;

    public async Task<PaymentProviderInitiationResult> InitiateAsync(Invoice invoice, Payment payment, InitiateInvoicePaymentRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            return PaymentProviderInitiationResult.FailedResult(
                "Ozow is not configured. Set Ozow:SiteCode, Ozow:ApiKey, Ozow:PrivateKey, Ozow:NotifyUrl and Ozow:IsTest.");
        }

        // Resolved once + reused so the hash, body, and metadata all
        // see the same value. IsConfigured guarantees HasValue above.
        var isTest = _settings.IsTest!.Value;

        // Stable, unique, traceable per the brief. Includes the
        // invoice + payment ids so a webhook with this reference can
        // be matched back to exactly one Payment row.
        var transactionReference = $"SF-{invoice.Id:N}-{payment.Id:N}";
        var bankReference = BuildBankReference(invoice, payment);

        // ─── TEMPORARY LIVE OZOW TEST OVERRIDE ─────────────────────────
        // Honoured only when IsTest=true AND TestAmountOverride > 0.
        // Mirrors the override onto payment.Amount so the webhook's
        // amount-mismatch guard still passes (override vs override),
        // and the resulting invoice ends up PartiallyPaid rather than
        // Paid — i.e. the R100 invoice is NOT marked fully paid just
        // because Ozow took R10.
        //
        // REMOVE Ozow__TestAmountOverride env var before production
        // launch. Each initiation that hits this branch logs a LOUD
        // warning so operators can spot it in CloudWatch.
        var originalAmount = payment.Amount;
        var overrideAmount = isTest && _settings.TestAmountOverride is decimal o && o > 0m ? o : (decimal?)null;
        if (overrideAmount.HasValue)
        {
            payment.Amount = overrideAmount.Value;
            _logger.LogWarning(
                "OZOW TEST AMOUNT OVERRIDE ACTIVE — payment {PaymentNumber} for invoice {InvoiceNumber} " +
                "charged R{Override} instead of R{Original}. Remove Ozow__TestAmountOverride before production.",
                payment.PaymentNumber, invoice.InvoiceNumber, overrideAmount.Value, originalAmount);
        }
        // ───────────────────────────────────────────────────────────────

        var successUrl = FirstNonEmpty(request.SuccessUrl, _settings.SuccessUrl);
        var cancelUrl  = FirstNonEmpty(request.CancelUrl,  _settings.CancelUrl);
        var errorUrl   = FirstNonEmpty(request.FailureUrl, _settings.ErrorUrl);
        var notifyUrl  = _settings.NotifyUrl;

        if (string.IsNullOrWhiteSpace(successUrl) || string.IsNullOrWhiteSpace(cancelUrl) || string.IsNullOrWhiteSpace(errorUrl))
        {
            return PaymentProviderInitiationResult.FailedResult(
                "Ozow success/cancel/error URLs are required. Pass them on the initiate request or configure Ozow:SuccessUrl / Ozow:CancelUrl / Ozow:ErrorUrl.");
        }

        var hash = OzowHashCalculator.BuildRequestHash(
            siteCode:             _settings.SiteCode,
            countryCode:          _settings.CountryCode,
            currencyCode:         _settings.CurrencyCode,
            amount:               payment.Amount,
            transactionReference: transactionReference,
            bankReference:        bankReference,
            cancelUrl:            cancelUrl,
            errorUrl:             errorUrl,
            successUrl:           successUrl,
            notifyUrl:            notifyUrl,
            isTest:               isTest,
            privateKey:           _settings.PrivateKey);

        var body = new OzowPostPaymentRequest
        {
            SiteCode             = _settings.SiteCode,
            CountryCode          = _settings.CountryCode,
            CurrencyCode         = _settings.CurrencyCode,
            Amount               = payment.Amount.ToString("F2", CultureInfo.InvariantCulture),
            TransactionReference = transactionReference,
            BankReference        = bankReference,
            CancelUrl            = cancelUrl,
            ErrorUrl             = errorUrl,
            SuccessUrl           = successUrl,
            NotifyUrl            = notifyUrl,
            IsTest               = isTest,
            HashCheck            = hash
        };

        var endpoint = ResolveEndpoint();
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        message.Headers.TryAddWithoutValidation("ApiKey", _settings.ApiKey);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);

            // Phase 53.2 — best-effort parse of the response body even
            // on a 4xx/5xx, so we can extract Ozow's `errorMessage`
            // into the structured log line. The body contains
            // server-supplied diagnostics only (no SmartFuture
            // secrets) — safe to log. ApiKey + PrivateKey are NEVER
            // included in the log payload.
            OzowPostPaymentResponse? parsedSafe = null;
            try { parsedSafe = JsonSerializer.Deserialize<OzowPostPaymentResponse>(rawBody, JsonOptions); }
            catch { /* body wasn't JSON — fall back to raw text in the log */ }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Ozow PostPaymentRequest HTTP {StatusCode} for {Reference} (endpoint {Endpoint}, isTest {IsTest}): errorMessage='{ErrorMessage}', body='{Body}'",
                    (int)response.StatusCode,
                    transactionReference,
                    endpoint,
                    isTest,
                    parsedSafe?.ErrorMessage ?? "(none)",
                    Truncate(rawBody, 500));
                // Surface Ozow's own error to the customer when present;
                // it's usually short + actionable ("Invalid SiteCode" etc).
                return PaymentProviderInitiationResult.FailedResult(
                    !string.IsNullOrWhiteSpace(parsedSafe?.ErrorMessage)
                        ? parsedSafe!.ErrorMessage!
                        : $"Ozow rejected the payment request (HTTP {(int)response.StatusCode}). Please try again.");
            }

            var parsed = parsedSafe ?? new OzowPostPaymentResponse();

            if (!string.IsNullOrWhiteSpace(parsed.ErrorMessage))
            {
                _logger.LogWarning(
                    "Ozow PostPaymentRequest 200-with-error for {Reference} (endpoint {Endpoint}, isTest {IsTest}): {Error}",
                    transactionReference, endpoint, isTest, parsed.ErrorMessage);
                return PaymentProviderInitiationResult.FailedResult(parsed.ErrorMessage!);
            }

            if (string.IsNullOrWhiteSpace(parsed.Url))
            {
                return PaymentProviderInitiationResult.FailedResult(
                    "Ozow accepted the request but did not return a redirect URL.");
            }

            // Snapshot the resolved transactionReference into MetadataJson
            // so the webhook handler can sanity-match it on inbound, and
            // so admin can re-issue the same hash for diagnostics. The
            // override fields ride along so the audit trail captures
            // "this was a test-amount-overridden charge" forever.
            var metadata = JsonSerializer.Serialize(new
            {
                transactionReference,
                bankReference,
                isTest = isTest,
                ozowPaymentRequestId = parsed.PaymentRequestId,
                successUrl,
                cancelUrl,
                errorUrl,
                notifyUrl,
                testAmountOverrideApplied = overrideAmount.HasValue,
                testAmountOverrideOriginalAmount = overrideAmount.HasValue ? originalAmount : (decimal?)null,
                testAmountOverrideAmount = overrideAmount
            }, JsonOptions);

            return PaymentProviderInitiationResult.Succeeded(
                providerReference:  transactionReference,
                providerCheckoutId: parsed.PaymentRequestId,
                redirectUrl:        parsed.Url,
                expiresAtUtc:       null,
                metadataJson:       metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Ozow PostPaymentRequest threw for {Reference} (endpoint {Endpoint}, isTest {IsTest})",
                transactionReference, endpoint, isTest);
            return PaymentProviderInitiationResult.FailedResult(
                "We couldn't reach Ozow. Please try again in a moment.");
        }
    }

    // Phase 53.2 — LIVE-only by default. The IsTest flag controls the
    // value sent to Ozow in the request body + hash; it no longer
    // selects the URL. If the operator wants to hit staging, they
    // must set Ozow:ApiUrl explicitly.
    private string ResolveEndpoint()
        => !string.IsNullOrWhiteSpace(_settings.ApiUrl) ? _settings.ApiUrl : LiveApiUrl;

    private static string FirstNonEmpty(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a! : b ?? string.Empty;

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);

    // Ozow shows BankReference on the customer's bank statement; max
    // 20 chars per Ozow docs, alphanumeric + dash/underscore safe.
    // We use the invoice number when short enough, falling back to the
    // payment number — both already pass that profile.
    private static string BuildBankReference(Invoice invoice, Payment payment)
    {
        var candidate = !string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? invoice.InvoiceNumber
            : payment.PaymentNumber;
        if (string.IsNullOrWhiteSpace(candidate)) candidate = "SmartFuture";
        return candidate.Length > 20 ? candidate[..20] : candidate;
    }

    /// <summary>Ozow's request body shape (PascalCase). Serialised as JSON.</summary>
    private class OzowPostPaymentRequest
    {
        public string SiteCode { get; set; } = string.Empty;
        public string CountryCode { get; set; } = string.Empty;
        public string CurrencyCode { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
        public string TransactionReference { get; set; } = string.Empty;
        public string BankReference { get; set; } = string.Empty;
        public string CancelUrl { get; set; } = string.Empty;
        public string ErrorUrl { get; set; } = string.Empty;
        public string SuccessUrl { get; set; } = string.Empty;
        public string NotifyUrl { get; set; } = string.Empty;
        public bool   IsTest { get; set; }
        public string HashCheck { get; set; } = string.Empty;
    }

    /// <summary>Ozow's response. Field names per current Ozow docs.</summary>
    private class OzowPostPaymentResponse
    {
        [JsonPropertyName("paymentRequestId")] public string? PaymentRequestId { get; set; }
        [JsonPropertyName("url")]              public string? Url { get; set; }
        [JsonPropertyName("errorMessage")]     public string? ErrorMessage { get; set; }
    }
}

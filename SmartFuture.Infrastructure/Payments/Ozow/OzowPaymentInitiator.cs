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
    //
    // Phase 53.4 — endpoint casing matches Ozow's published docs
    // (`PostPaymentRequest`, PascalCase). Lowercase worked too because
    // Ozow normalises, but matching docs avoids confusion when
    // grepping their reference + ours side-by-side.
    private const string LiveApiUrl = "https://api.ozow.com/PostPaymentRequest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Phase 53.4 — Ozow sometimes returns PascalCase fields on
        // errors (e.g. "ErrorMessage": "Invalid HashCheck") and
        // camelCase on success ("paymentRequestId" / "url"). Without
        // case-insensitive parsing the error path silently dropped
        // the message and we surfaced a generic "Ozow rejected …"
        // string. Insensitive parsing covers both shapes safely.
        PropertyNameCaseInsensitive = true
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

        // Pre-format the two values that have casing/culture pitfalls
        // so the hash input and the body field use the IDENTICAL
        // string. Both functions are pure + dependency-free.
        var amountString = OzowHashCalculator.FormatAmount(payment.Amount);  // "10.00" (F2, InvariantCulture)
        var isTestString = isTest ? "true" : "false";                        // lowercase

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

        // ─── [OzowHashDebug] ──────────────────────────────────────────
        // Emit each field in EXACT hash order with its safe value so
        // a reviewer can verify the input character-by-character
        // against Ozow's docs (siteCode + countryCode + currencyCode
        // + amount + transactionReference + bankReference + cancelUrl
        // + errorUrl + successUrl + notifyUrl + isTest + PrivateKey,
        // then lowercase + SHA512).
        //
        // SiteCode is masked. PrivateKey is reported as length only —
        // never logged. The hash itself is logged length-only too.
        _logger.LogInformation(
            "[OzowHashDebug] order=siteCode|countryCode|currencyCode|amount|transactionReference|bankReference|cancelUrl|errorUrl|successUrl|notifyUrl|isTest|+PrivateKey  " +
            "siteCode={SiteCodeMasked} countryCode={CountryCode} currencyCode={CurrencyCode} amount={Amount} " +
            "transactionReference={TransactionReference} bankReference={BankReference} " +
            "cancelUrl={CancelUrl} errorUrl={ErrorUrl} successUrl={SuccessUrl} notifyUrl={NotifyUrl} " +
            "isTest={IsTest} privateKeyLength={PrivateKeyLength} hashLength={HashLength}",
            MaskSiteCode(_settings.SiteCode),
            _settings.CountryCode,
            _settings.CurrencyCode,
            amountString,
            transactionReference,
            bankReference,
            cancelUrl,
            errorUrl,
            successUrl,
            notifyUrl,
            isTestString,
            _settings.PrivateKey?.Length ?? 0,
            hash.Length);

        var body = new OzowPostPaymentRequest
        {
            SiteCode             = _settings.SiteCode,
            CountryCode          = _settings.CountryCode,
            CurrencyCode         = _settings.CurrencyCode,
            // Phase 53.4 — reuse the pre-formatted amountString that
            // the hash also saw. Guarantees byte-identical input on
            // both sides — culture drift can no longer break the hash.
            Amount               = amountString,
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

        // ─── [OzowRequestDebug] ────────────────────────────────────────
        // Sanitized snapshot of what we're about to send. SiteCode is
        // masked (first 3 + last 2 chars). HashCheck is reported as
        // length-only — the SHA512 itself is irreversible but we still
        // avoid putting it in the log payload. ApiKey + PrivateKey
        // are NEVER logged. Body field names mirror Ozow's docs so the
        // log lines are easy to compare against their reference.
        _logger.LogInformation(
            "[OzowRequestDebug] endpoint={Endpoint} siteCode={SiteCodeMasked} countryCode={CountryCode} currencyCode={CurrencyCode} " +
            "amount={Amount} transactionReference={TransactionReference} bankReference={BankReference} " +
            "cancelUrl={CancelUrl} errorUrl={ErrorUrl} successUrl={SuccessUrl} notifyUrl={NotifyUrl} " +
            "isTest={IsTest} hashCheckLength={HashLength}",
            endpoint,
            MaskSiteCode(_settings.SiteCode),
            _settings.CountryCode,
            _settings.CurrencyCode,
            body.Amount,
            transactionReference,
            bankReference,
            cancelUrl,
            errorUrl,
            successUrl,
            notifyUrl,
            isTest,
            hash.Length);

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            // Phase 53.2 — best-effort parse of the response body even
            // on a 4xx/5xx, so we can extract Ozow's `errorMessage`
            // into the structured log line. The body contains
            // server-supplied diagnostics only (no SmartFuture
            // secrets) — safe to log. ApiKey + PrivateKey are NEVER
            // included in the log payload.
            OzowPostPaymentResponse? parsedSafe = null;
            try { parsedSafe = JsonSerializer.Deserialize<OzowPostPaymentResponse>(rawBody, JsonOptions); }
            catch { /* body wasn't JSON — fall back to raw text in the log */ }

            // ─── [OzowResponseDebug] ────────────────────────────────
            // Always logged regardless of status, so we can see Ozow's
            // full reply during debugging. Raw body truncated to 1000
            // chars per the brief.
            _logger.LogInformation(
                "[OzowResponseDebug] httpStatus={StatusCode} transactionReference={TransactionReference} " +
                "endpoint={Endpoint} ozowErrorMessage='{ErrorMessage}' paymentRequestId={PaymentRequestId} " +
                "url={Url} rawBody='{RawBody}'",
                statusCode,
                transactionReference,
                endpoint,
                parsedSafe?.ErrorMessage ?? "(none)",
                parsedSafe?.PaymentRequestId ?? "(none)",
                parsedSafe?.Url ?? "(none)",
                Truncate(rawBody, 1000));

            if (!response.IsSuccessStatusCode)
            {
                // Phase 53.4 — preference order for the customer-visible
                // failureReason:
                //   1. Ozow's parsed `errorMessage` field (best — short
                //      and actionable, e.g. "Invalid HashCheck").
                //   2. The raw body text, truncated, if it's short and
                //      non-JSON (Ozow sometimes returns a plain string
                //      on 400). Better than a generic HTTP message.
                //   3. Generic HTTP-status fallback (last resort).
                var ozowError = !string.IsNullOrWhiteSpace(parsedSafe?.ErrorMessage)
                    ? parsedSafe!.ErrorMessage!
                    : null;
                var rawBodyShort = !string.IsNullOrWhiteSpace(rawBody) && rawBody.Length <= 300 && parsedSafe is null
                    ? rawBody.Trim().Trim('"')
                    : null;
                var failureReason = ozowError
                    ?? rawBodyShort
                    ?? $"Ozow rejected the payment request (HTTP {statusCode}).";

                return PaymentProviderInitiationResult.FailedResult(
                    failureReason:        failureReason,
                    providerStatusCode:   statusCode,
                    providerErrorMessage: ozowError ?? rawBodyShort,
                    providerEndpoint:     endpoint,
                    providerIsTest:       isTest,
                    providerReference:    transactionReference,
                    providerRawResponseSnippet: Truncate(rawBody, 500));
            }

            var parsed = parsedSafe ?? new OzowPostPaymentResponse();

            if (!string.IsNullOrWhiteSpace(parsed.ErrorMessage))
            {
                return PaymentProviderInitiationResult.FailedResult(
                    failureReason: parsed.ErrorMessage!,
                    providerStatusCode: statusCode,
                    providerErrorMessage: parsed.ErrorMessage,
                    providerEndpoint: endpoint,
                    providerIsTest: isTest,
                    providerReference: transactionReference,
                    providerRawResponseSnippet: Truncate(rawBody, 500));
            }

            if (string.IsNullOrWhiteSpace(parsed.Url))
            {
                return PaymentProviderInitiationResult.FailedResult(
                    failureReason: "Ozow accepted the request but did not return a redirect URL.",
                    providerStatusCode: statusCode,
                    providerErrorMessage: null,
                    providerEndpoint: endpoint,
                    providerIsTest: isTest,
                    providerReference: transactionReference,
                    providerRawResponseSnippet: Truncate(rawBody, 500));
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
                metadataJson:       metadata,
                providerStatusCode: statusCode,
                providerEndpoint:   endpoint,
                providerIsTest:     isTest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[OzowResponseDebug] transport-error for {Reference} (endpoint {Endpoint}, isTest {IsTest}): {ExceptionMessage}",
                transactionReference, endpoint, isTest, ex.Message);
            return PaymentProviderInitiationResult.FailedResult(
                failureReason: $"We couldn't reach Ozow ({ex.GetType().Name}).",
                providerStatusCode: null,
                providerErrorMessage: ex.Message,
                providerEndpoint: endpoint,
                providerIsTest: isTest,
                providerReference: transactionReference);
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

    // Phase 53.3 — site-code mask for safe logging. SiteCode isn't a
    // secret on its own (it appears in the body that Ozow sees) but
    // we still mask it in logs so a leak doesn't reveal which merchant
    // an environment is wired to.
    private static string MaskSiteCode(string? siteCode)
    {
        if (string.IsNullOrEmpty(siteCode)) return "(empty)";
        if (siteCode.Length <= 5) return new string('*', siteCode.Length);
        return $"{siteCode[..3]}***{siteCode[^2..]}";
    }

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

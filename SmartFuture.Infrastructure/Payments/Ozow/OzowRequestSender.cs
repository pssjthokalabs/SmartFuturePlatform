using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Ozow;

namespace SmartFuture.Infrastructure.Payments.Ozow;

/// <summary>
/// Single source of truth for the Ozow <c>PostPaymentRequest</c> wire
/// protocol: hash input, request body, HTTP transport, response parsing
/// and the diagnostic log lines.
///
/// Extracted from <see cref="OzowPaymentInitiator"/> when Ozow gained a
/// second caller. Two callers now share it:
///
///   • <see cref="OzowPaymentInitiator"/>       — invoice payments. An
///     Invoice + Payment + PaymentInitiation already exist; the webhook
///     settles them.
///   • <see cref="OzowIntentInitiationService"/> — new-order checkout.
///     NOTHING exists yet but an OrderIntent; the webhook materialises
///     Order + Invoice + Payment on success.
///
/// Both paths MUST produce a byte-identical hash for the same inputs, so
/// the hash + body construction deliberately lives here and nowhere
/// else. A divergence between two hand-maintained copies would surface
/// as an intermittent "Invalid HashCheck" on one flow only, which is
/// exactly the kind of bug that is miserable to find in production.
///
/// This class owns NO business rules: no amount overrides, no reference
/// formats, no entity access. Callers decide what to charge and what to
/// call the transaction; this just signs it and posts it.
/// </summary>
public class OzowRequestSender
{
    // Phase 53.2 — staging is intentionally NOT auto-picked. We don't
    // have staging-issued credentials, and using live keys against
    // staging silently fails ("merchant not found"). To hit staging,
    // set Ozow:ApiUrl explicitly.
    //
    // Phase 53.4 — endpoint casing matches Ozow's published docs
    // (`PostPaymentRequest`, PascalCase).
    private const string LiveApiUrl = "https://api.ozow.com/PostPaymentRequest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Ozow returns PascalCase on errors ("ErrorMessage": "Invalid
        // HashCheck") and camelCase on success ("paymentRequestId" /
        // "url"). Insensitive parsing covers both shapes safely.
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OzowSettings _settings;
    private readonly ILogger<OzowRequestSender> _logger;

    public OzowRequestSender(
        HttpClient httpClient,
        IOptions<OzowSettings> settings,
        ILogger<OzowRequestSender> logger)
    {
        _httpClient = httpClient;
        _settings   = settings.Value;
        _logger     = logger;
    }

    /// <summary>Endpoint this sender will POST to. Exposed so callers can
    /// include it in their own failure diagnostics.</summary>
    public string Endpoint
        => !string.IsNullOrWhiteSpace(_settings.ApiUrl) ? _settings.ApiUrl! : LiveApiUrl;

    /// <summary>
    /// Sign <paramref name="spec"/>, POST it to Ozow, and map the reply.
    /// Never throws — transport failures come back as a failed outcome so
    /// callers can surface a clean message instead of a 500.
    /// </summary>
    public async Task<OzowSendOutcome> SendAsync(
        OzowRequestSpec spec,
        CancellationToken cancellationToken = default)
    {
        var endpoint = Endpoint;

        // Pre-format the two values with casing/culture pitfalls so the
        // hash input and the body field use the IDENTICAL string.
        var amountString = OzowHashCalculator.FormatAmount(spec.Amount);  // "10.00" (F2, Invariant)
        var isTestString = spec.IsTest ? "true" : "false";                // lowercase

        var hash = OzowHashCalculator.BuildRequestHash(
            siteCode:             _settings.SiteCode,
            countryCode:          _settings.CountryCode,
            currencyCode:         _settings.CurrencyCode,
            amount:               spec.Amount,
            transactionReference: spec.TransactionReference,
            bankReference:        spec.BankReference,
            cancelUrl:            spec.CancelUrl,
            errorUrl:             spec.ErrorUrl,
            successUrl:           spec.SuccessUrl,
            notifyUrl:            spec.NotifyUrl,
            isTest:               spec.IsTest,
            privateKey:           _settings.PrivateKey);

        // ─── [OzowHashDebug] ──────────────────────────────────────────
        // Each field in EXACT hash order with its safe value, so a
        // reviewer can verify the input character-by-character against
        // Ozow's docs. SiteCode masked; PrivateKey length-only, never
        // logged; hash length-only.
        _logger.LogInformation(
            "[OzowHashDebug] flow={Flow} order=siteCode|countryCode|currencyCode|amount|transactionReference|bankReference|cancelUrl|errorUrl|successUrl|notifyUrl|isTest|+PrivateKey  " +
            "siteCode={SiteCodeMasked} countryCode={CountryCode} currencyCode={CurrencyCode} amount={Amount} " +
            "transactionReference={TransactionReference} bankReference={BankReference} " +
            "cancelUrl={CancelUrl} errorUrl={ErrorUrl} successUrl={SuccessUrl} notifyUrl={NotifyUrl} " +
            "isTest={IsTest} privateKeyLength={PrivateKeyLength} hashLength={HashLength} " +
            "transactionReferenceLength={TransactionReferenceLength} bankReferenceLength={BankReferenceLength}",
            spec.Flow,
            MaskSiteCode(_settings.SiteCode),
            _settings.CountryCode,
            _settings.CurrencyCode,
            amountString,
            spec.TransactionReference,
            spec.BankReference,
            spec.CancelUrl,
            spec.ErrorUrl,
            spec.SuccessUrl,
            spec.NotifyUrl,
            isTestString,
            _settings.PrivateKey?.Length ?? 0,
            hash.Length,
            spec.TransactionReference.Length,
            spec.BankReference.Length);

        var body = new OzowPostPaymentRequest
        {
            SiteCode             = _settings.SiteCode,
            CountryCode          = _settings.CountryCode,
            CurrencyCode         = _settings.CurrencyCode,
            // Reuse the pre-formatted amountString the hash also saw —
            // guarantees byte-identical input on both sides.
            Amount               = amountString,
            TransactionReference = spec.TransactionReference,
            BankReference        = spec.BankReference,
            CancelUrl            = spec.CancelUrl,
            ErrorUrl             = spec.ErrorUrl,
            SuccessUrl           = spec.SuccessUrl,
            NotifyUrl            = spec.NotifyUrl,
            IsTest               = spec.IsTest,
            HashCheck            = hash
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        message.Headers.TryAddWithoutValidation("ApiKey", _settings.ApiKey);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        // ─── [OzowRequestDebug] ────────────────────────────────────────
        // Sanitized snapshot of what we're about to send. SiteCode
        // masked; HashCheck length-only. ApiKey + PrivateKey NEVER
        // logged. Field names mirror Ozow's docs for easy comparison.
        _logger.LogInformation(
            "[OzowRequestDebug] flow={Flow} endpoint={Endpoint} siteCode={SiteCodeMasked} countryCode={CountryCode} currencyCode={CurrencyCode} " +
            "amount={Amount} transactionReference={TransactionReference} bankReference={BankReference} " +
            "cancelUrl={CancelUrl} errorUrl={ErrorUrl} successUrl={SuccessUrl} notifyUrl={NotifyUrl} " +
            "isTest={IsTest} hashCheckLength={HashLength}",
            spec.Flow,
            endpoint,
            MaskSiteCode(_settings.SiteCode),
            _settings.CountryCode,
            _settings.CurrencyCode,
            body.Amount,
            spec.TransactionReference,
            spec.BankReference,
            spec.CancelUrl,
            spec.ErrorUrl,
            spec.SuccessUrl,
            spec.NotifyUrl,
            spec.IsTest,
            hash.Length);

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var rawBody    = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            // Best-effort parse even on 4xx/5xx so Ozow's `errorMessage`
            // reaches the structured log. Body carries server-supplied
            // diagnostics only — no SmartFuture secrets.
            OzowPostPaymentResponse? parsed = null;
            try { parsed = JsonSerializer.Deserialize<OzowPostPaymentResponse>(rawBody, JsonOptions); }
            catch { /* not JSON — fall back to raw text in the log */ }

            // ─── [OzowResponseDebug] ────────────────────────────────
            _logger.LogInformation(
                "[OzowResponseDebug] flow={Flow} httpStatus={StatusCode} transactionReference={TransactionReference} " +
                "endpoint={Endpoint} ozowErrorMessage='{ErrorMessage}' paymentRequestId={PaymentRequestId} " +
                "url={Url} rawBody='{RawBody}'",
                spec.Flow,
                statusCode,
                spec.TransactionReference,
                endpoint,
                parsed?.ErrorMessage ?? "(none)",
                parsed?.PaymentRequestId ?? "(none)",
                parsed?.Url ?? "(none)",
                Truncate(rawBody, 1000));

            if (!response.IsSuccessStatusCode)
            {
                // Preference order for the customer-visible reason:
                //   1. Ozow's parsed `errorMessage` (short + actionable).
                //   2. Short non-JSON raw body (Ozow sometimes returns a
                //      plain string on 400).
                //   3. Generic HTTP-status fallback.
                var ozowError = !string.IsNullOrWhiteSpace(parsed?.ErrorMessage) ? parsed!.ErrorMessage! : null;
                var rawShort  = !string.IsNullOrWhiteSpace(rawBody) && rawBody.Length <= 300 && parsed is null
                    ? rawBody.Trim().Trim('"')
                    : null;

                return OzowSendOutcome.Failed(
                    failureReason:  ozowError ?? rawShort ?? $"Ozow rejected the payment request (HTTP {statusCode}).",
                    statusCode:     statusCode,
                    errorMessage:   ozowError ?? rawShort,
                    endpoint:       endpoint,
                    rawBodySnippet: Truncate(rawBody, 500));
            }

            if (!string.IsNullOrWhiteSpace(parsed?.ErrorMessage))
            {
                return OzowSendOutcome.Failed(
                    failureReason:  parsed!.ErrorMessage!,
                    statusCode:     statusCode,
                    errorMessage:   parsed.ErrorMessage,
                    endpoint:       endpoint,
                    rawBodySnippet: Truncate(rawBody, 500));
            }

            if (string.IsNullOrWhiteSpace(parsed?.Url))
            {
                return OzowSendOutcome.Failed(
                    failureReason:  "Ozow accepted the request but did not return a redirect URL.",
                    statusCode:     statusCode,
                    errorMessage:   null,
                    endpoint:       endpoint,
                    rawBodySnippet: Truncate(rawBody, 500));
            }

            return OzowSendOutcome.Succeeded(
                redirectUrl:      parsed!.Url!,
                paymentRequestId: parsed.PaymentRequestId,
                statusCode:       statusCode,
                endpoint:         endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[OzowResponseDebug] flow={Flow} transport-error for {Reference} (endpoint {Endpoint}, isTest {IsTest}): {ExceptionMessage}",
                spec.Flow, spec.TransactionReference, endpoint, spec.IsTest, ex.Message);

            return OzowSendOutcome.Failed(
                failureReason: $"We couldn't reach Ozow ({ex.GetType().Name}).",
                statusCode:    null,
                errorMessage:  ex.Message,
                endpoint:      endpoint,
                rawBodySnippet: null);
        }
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);

    // SiteCode isn't a secret on its own (Ozow sees it in the body) but
    // we mask it so a log leak doesn't reveal which merchant an
    // environment is wired to.
    private static string MaskSiteCode(string? siteCode)
    {
        if (string.IsNullOrEmpty(siteCode)) return "(empty)";
        if (siteCode.Length <= 5) return new string('*', siteCode.Length);
        return $"{siteCode[..3]}***{siteCode[^2..]}";
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
        public bool IsTest { get; set; }
        public string HashCheck { get; set; } = string.Empty;
    }

    /// <summary>Ozow's reply. Success → `url` + `paymentRequestId`;
    /// failure → `errorMessage`.</summary>
    private class OzowPostPaymentResponse
    {
        public string? PaymentRequestId { get; set; }
        public string? Url { get; set; }
        public string? ErrorMessage { get; set; }
    }
}

/// <summary>
/// Everything the Ozow wire protocol needs for one PostPaymentRequest.
/// The caller has already decided the amount and the references —
/// <see cref="OzowRequestSender"/> only signs and posts.
/// </summary>
public class OzowRequestSpec
{
    /// <summary>Amount actually charged (post-override). Formatted F2
    /// Invariant for both hash and body.</summary>
    public decimal Amount { get; set; }

    /// <summary>Max 50 chars — Ozow rejects longer. Echoed back on the
    /// notify, so this is the key the webhook resolves on.</summary>
    public string TransactionReference { get; set; } = string.Empty;

    /// <summary>Max 20 chars — shown on the customer's bank statement.</summary>
    public string BankReference { get; set; } = string.Empty;

    public string SuccessUrl { get; set; } = string.Empty;
    public string CancelUrl { get; set; } = string.Empty;
    public string ErrorUrl { get; set; } = string.Empty;
    public string NotifyUrl { get; set; } = string.Empty;

    public bool IsTest { get; set; }

    /// <summary>"invoice" | "intent" — log tag only, never sent to Ozow.
    /// Lets an operator grep one flow's hash/request/response triple.</summary>
    public string Flow { get; set; } = "invoice";
}

/// <summary>Provider-agnostic result of one PostPaymentRequest.</summary>
public class OzowSendOutcome
{
    public bool Success { get; private init; }
    public string? FailureReason { get; private init; }
    public string? RedirectUrl { get; private init; }
    public string? PaymentRequestId { get; private init; }
    public int? ProviderStatusCode { get; private init; }
    public string? ProviderErrorMessage { get; private init; }
    public string Endpoint { get; private init; } = string.Empty;
    public string? RawBodySnippet { get; private init; }

    public static OzowSendOutcome Succeeded(
        string redirectUrl, string? paymentRequestId, int statusCode, string endpoint) =>
        new()
        {
            Success            = true,
            RedirectUrl        = redirectUrl,
            PaymentRequestId   = paymentRequestId,
            ProviderStatusCode = statusCode,
            Endpoint           = endpoint
        };

    public static OzowSendOutcome Failed(
        string failureReason, int? statusCode, string? errorMessage, string endpoint, string? rawBodySnippet) =>
        new()
        {
            Success              = false,
            FailureReason        = failureReason,
            ProviderStatusCode   = statusCode,
            ProviderErrorMessage = errorMessage,
            Endpoint             = endpoint,
            RawBodySnippet       = rawBodySnippet
        };
}

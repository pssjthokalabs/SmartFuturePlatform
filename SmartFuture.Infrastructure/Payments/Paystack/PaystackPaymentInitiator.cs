using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Infrastructure.Payments.Paystack;

/// <summary>
/// Paystack <c>POST /transaction/initialize</c> initiator.
///
/// Builds the initialize body server-side (the secret key never leaves
/// the API tier), POSTs it to Paystack, and returns the
/// <c>authorization_url</c> Paystack responds with so the customer can
/// be redirected (popup on portal, WebView on mobile).
///
/// Webhook verification — including the HMAC-SHA512 signature check —
/// lives in <see cref="PaystackNotifyHandler"/>. This class owns only
/// the HTTP transport, the amount/reference shaping, and the response
/// mapping.
/// </summary>
public class PaystackPaymentInitiator : IPaymentInitiator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly PaystackSettings _settings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PaystackPaymentInitiator> _logger;

    public PaystackPaymentInitiator(
        HttpClient httpClient,
        IOptions<PaystackSettings> settings,
        IHostEnvironment env,
        ILogger<PaystackPaymentInitiator> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _env = env;
        _logger = logger;
    }

    public PaymentProviderType Provider => PaymentProviderType.Paystack;

    public async Task<PaymentProviderInitiationResult> InitiateAsync(
        Invoice invoice, Payment payment, InitiateInvoicePaymentRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return PaymentProviderInitiationResult.FailedResult(
                "Paystack is not enabled. Set Paystack__Enabled=true and provide Paystack__SecretKey + Paystack__CallbackUrl.");
        }

        if (!_settings.IsConfigured)
        {
            return PaymentProviderInitiationResult.FailedResult(
                "Paystack is not configured. Provide Paystack__SecretKey and Paystack__CallbackUrl.");
        }

        // ─── Email source-of-truth ────────────────────────────────────
        // Paystack requires an email. We deliberately pull from the
        // server-recorded invoice/order, NOT from anything the client
        // sent. Falling back to user → coverage-request keeps the
        // pre-customer-profile flows working.
        var email = ResolveCustomerEmail(invoice);
        if (string.IsNullOrWhiteSpace(email))
        {
            return PaymentProviderInitiationResult.FailedResult(
                "Cannot start a Paystack payment without a customer email on the invoice.");
        }

        // ─── Test-amount override (non-production only) ───────────────
        // Mirrors the Ozow pattern. Production hard-blocks regardless
        // of the flag. Payment.Amount mirrors the sent value so the
        // webhook amount check still passes.
        var originalAmount = payment.Amount;
        var overrideActive = _settings.UseTestAmountOverride
                          && _settings.TestAmount is > 0m
                          && !_env.IsProduction();
        if (overrideActive)
        {
            payment.Amount = _settings.TestAmount!.Value;
            _logger.LogWarning(
                "[PaystackTestAmountOverride] invoiceAmount={InvoiceAmount} sentAmount={SentAmount} " +
                "payment={PaymentNumber} invoice={InvoiceNumber} env={Environment}",
                originalAmount, _settings.TestAmount.Value,
                payment.PaymentNumber, invoice.InvoiceNumber, _env.EnvironmentName);
        }
        else if (_settings.UseTestAmountOverride && _env.IsProduction())
        {
            _logger.LogError(
                "[PaystackTestAmountOverride] BLOCKED — UseTestAmountOverride is true but environment is Production. " +
                "Using real invoice amount {Amount}. Remove Paystack__UseTestAmountOverride before production.",
                originalAmount);
        }

        // ─── Reference + amount-in-subunits ───────────────────────────
        // Reference must be Paystack-safe: alphanumeric + -.= per their
        // docs. SF-PAY-… is already compliant.
        var reference = BuildReference(payment);
        if (!IsPaystackReferenceSafe(reference))
        {
            _logger.LogError("[PaystackRequestDebug] reference contains unsafe characters: '{Reference}'", reference);
            return PaymentProviderInitiationResult.FailedResult(
                "Generated Paystack reference is invalid. Allowed: a–z, A–Z, 0–9, '-', '.', '='.");
        }

        var amountSubunits = ToSubunits(payment.Amount);
        if (amountSubunits <= 0)
        {
            return PaymentProviderInitiationResult.FailedResult(
                "Paystack requires a positive amount in subunits.");
        }

        var callbackUrl = !string.IsNullOrWhiteSpace(request.SuccessUrl)
            ? request.SuccessUrl!
            : _settings.CallbackUrl;
        var cancelUrl = !string.IsNullOrWhiteSpace(request.CancelUrl)
            ? request.CancelUrl!
            : _settings.CancelUrl;

        var currency = string.IsNullOrWhiteSpace(_settings.Currency) ? "ZAR" : _settings.Currency.Trim().ToUpperInvariant();

        // metadata.cancel_action is the documented hook the hosted
        // Paystack page uses to honour a custom cancel destination.
        var metadata = new PaystackInitializeMetadata
        {
            InvoiceId = invoice.Id.ToString(),
            InvoiceNumber = invoice.InvoiceNumber,
            PaymentId = payment.Id.ToString(),
            PaymentNumber = payment.PaymentNumber,
            CustomerId = invoice.Order?.UserId.ToString(),
            Provider = "Paystack",
            CancelAction = string.IsNullOrWhiteSpace(cancelUrl) ? null : cancelUrl,
            SmartFutureWebhookMode = request.SuppressWebhookApplication == true ? "ValidateOnly" : "ApplyNormally"
        };

        var body = new PaystackInitializeRequest
        {
            Email = email,
            Amount = amountSubunits,
            Currency = currency,
            Reference = reference,
            CallbackUrl = string.IsNullOrWhiteSpace(callbackUrl) ? null : callbackUrl,
            Channels = _settings.AllowedChannels is { Length: > 0 } chs ? chs : null,
            Metadata = metadata
        };

        // ─── [PaystackApiDebug] / [PaystackRequestDebug] ──────────────
        // Sanitized snapshot. Email is logged (operations need to
        // correlate it). Secret key is NEVER logged.
        _logger.LogInformation(
            "[PaystackRequestDebug] endpoint={Endpoint} reference={Reference} amountSubunits={AmountSubunits} " +
            "currency={Currency} email={Email} callbackUrl={CallbackUrl} channels={Channels} " +
            "invoice={InvoiceNumber} payment={PaymentNumber} testAmountOverrideApplied={OverrideApplied}",
            _settings.InitializeUrl, reference, amountSubunits, currency, email,
            callbackUrl ?? "(default)", _settings.AllowedChannels is { Length: > 0 } ? string.Join(",", _settings.AllowedChannels) : "(default)",
            invoice.InvoiceNumber, payment.PaymentNumber, overrideActive);

        using var message = new HttpRequestMessage(HttpMethod.Post, _settings.InitializeUrl)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SecretKey);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            PaystackInitializeResponse? parsedSafe = null;
            try { parsedSafe = JsonSerializer.Deserialize<PaystackInitializeResponse>(rawBody, JsonOptions); }
            catch { /* body wasn't JSON */ }

            _logger.LogInformation(
                "[PaystackResponseDebug] httpStatus={StatusCode} reference={Reference} paystackStatus={PaystackStatus} " +
                "message='{Message}' authorizationUrlPresent={AuthUrlPresent} accessCodePresent={AccessCodePresent} " +
                "returnedReference={ReturnedRef} rawBody='{RawBody}'",
                statusCode, reference,
                parsedSafe?.Status?.ToString() ?? "(none)",
                parsedSafe?.Message ?? "(none)",
                !string.IsNullOrWhiteSpace(parsedSafe?.Data?.AuthorizationUrl),
                !string.IsNullOrWhiteSpace(parsedSafe?.Data?.AccessCode),
                parsedSafe?.Data?.Reference ?? "(none)",
                Truncate(rawBody, 1000));

            if (!response.IsSuccessStatusCode || parsedSafe is null || parsedSafe.Status != true)
            {
                var failureReason = !string.IsNullOrWhiteSpace(parsedSafe?.Message)
                    ? parsedSafe!.Message!
                    : $"Paystack rejected the initialize request (HTTP {statusCode}).";

                return PaymentProviderInitiationResult.FailedResult(
                    failureReason: failureReason,
                    providerStatusCode: statusCode,
                    providerErrorMessage: parsedSafe?.Message,
                    providerEndpoint: _settings.InitializeUrl,
                    providerIsTest: _settings.IsTestKey ?? _settings.UseTestMode,
                    providerReference: reference,
                    providerRawResponseSnippet: Truncate(rawBody, 500));
            }

            if (string.IsNullOrWhiteSpace(parsedSafe.Data?.AuthorizationUrl))
            {
                return PaymentProviderInitiationResult.FailedResult(
                    failureReason: "Paystack accepted the request but did not return an authorization_url.",
                    providerStatusCode: statusCode,
                    providerErrorMessage: parsedSafe.Message,
                    providerEndpoint: _settings.InitializeUrl,
                    providerIsTest: _settings.IsTestKey ?? _settings.UseTestMode,
                    providerReference: reference,
                    providerRawResponseSnippet: Truncate(rawBody, 500));
            }

            var metadataJson = JsonSerializer.Serialize(new
            {
                reference,
                paystackReference = parsedSafe.Data?.Reference,
                accessCode = parsedSafe.Data?.AccessCode,
                amountSubunits,
                currency,
                callbackUrl,
                cancelUrl,
                isTestKey = _settings.IsTestKey,
                testAmountOverrideApplied = overrideActive,
                testAmountOverrideOriginalAmount = overrideActive ? originalAmount : (decimal?)null,
                testAmountOverrideAmount = overrideActive ? _settings.TestAmount : null,
                channels = _settings.AllowedChannels is { Length: > 0 } ? _settings.AllowedChannels : null
            }, JsonOptions);

            return PaymentProviderInitiationResult.Succeeded(
                providerReference: reference,
                providerCheckoutId: parsedSafe.Data?.AccessCode,
                redirectUrl: parsedSafe.Data!.AuthorizationUrl,
                expiresAtUtc: null,
                metadataJson: metadataJson,
                providerStatusCode: statusCode,
                providerEndpoint: _settings.InitializeUrl,
                providerIsTest: _settings.IsTestKey ?? _settings.UseTestMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PaystackResponseDebug] transport-error for {Reference} (endpoint {Endpoint}): {ExceptionMessage}",
                reference, _settings.InitializeUrl, ex.Message);
            return PaymentProviderInitiationResult.FailedResult(
                failureReason: $"We couldn't reach Paystack ({ex.GetType().Name}).",
                providerStatusCode: null,
                providerErrorMessage: ex.Message,
                providerEndpoint: _settings.InitializeUrl,
                providerIsTest: _settings.IsTestKey ?? _settings.UseTestMode,
                providerReference: reference);
        }
    }

    // ─── helpers ──────────────────────────────────────────────────────

    // ZAR → kobo (subunits). Paystack expects amount as an integer in
    // the smallest currency unit; for ZAR that's cents. Use F0 string
    // multiplication with InvariantCulture-safe Math.Round to avoid
    // floating-point drift on values like 99.99 * 100.
    internal static long ToSubunits(decimal amount)
        => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static string BuildReference(Payment payment)
    {
        var paymentRef = !string.IsNullOrWhiteSpace(payment.PaymentNumber)
            ? payment.PaymentNumber
            : payment.Id.ToString("N")[..12];
        // SF-PAY-<paymentNumber>. Paystack-safe chars only. We keep
        // the prefix short so the full reference fits the 100-char
        // Paystack limit even with long payment numbers.
        return $"SF-{paymentRef}";
    }

    private static bool IsPaystackReferenceSafe(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 100) return false;
        foreach (var c in reference)
        {
            if (char.IsLetterOrDigit(c)) continue;
            if (c is '-' or '.' or '=') continue;
            return false;
        }
        return true;
    }

    private static string? ResolveCustomerEmail(Invoice invoice)
    {
        // Order email is the customer-confirmed value captured during
        // checkout. If that's empty, fall back to the linked user's
        // identity email. We do NOT trust anything from the client
        // request — the initiator only ever reads server state.
        var orderEmail = invoice.Order?.Email;
        if (!string.IsNullOrWhiteSpace(orderEmail)) return orderEmail.Trim();
        var userEmail = invoice.Order?.User?.Email;
        if (!string.IsNullOrWhiteSpace(userEmail)) return userEmail.Trim();
        return null;
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);

    // ─── wire DTOs (private) ──────────────────────────────────────────

    private class PaystackInitializeRequest
    {
        [JsonPropertyName("email")]        public string Email { get; set; } = string.Empty;
        [JsonPropertyName("amount")]       public long Amount { get; set; }
        [JsonPropertyName("currency")]     public string Currency { get; set; } = "ZAR";
        [JsonPropertyName("reference")]    public string Reference { get; set; } = string.Empty;
        [JsonPropertyName("callback_url")] public string? CallbackUrl { get; set; }
        [JsonPropertyName("channels")]     public string[]? Channels { get; set; }
        [JsonPropertyName("metadata")]     public PaystackInitializeMetadata? Metadata { get; set; }
    }

    private class PaystackInitializeMetadata
    {
        [JsonPropertyName("invoiceId")]     public string? InvoiceId { get; set; }
        [JsonPropertyName("invoiceNumber")] public string? InvoiceNumber { get; set; }
        [JsonPropertyName("paymentId")]     public string? PaymentId { get; set; }
        [JsonPropertyName("paymentNumber")] public string? PaymentNumber { get; set; }
        [JsonPropertyName("customerId")]    public string? CustomerId { get; set; }
        [JsonPropertyName("provider")]      public string? Provider { get; set; }
        [JsonPropertyName("cancel_action")] public string? CancelAction { get; set; }
        // Informational echo only — see PaystackNotifyHandler. The
        // authoritative decision lives on PaymentInitiation.WebhookApplyMode.
        [JsonPropertyName("smartfutureWebhookMode")] public string? SmartFutureWebhookMode { get; set; }
    }

    private class PaystackInitializeResponse
    {
        [JsonPropertyName("status")]  public bool? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")]    public PaystackInitializeResponseData? Data { get; set; }
    }

    private class PaystackInitializeResponseData
    {
        [JsonPropertyName("authorization_url")] public string? AuthorizationUrl { get; set; }
        [JsonPropertyName("access_code")]       public string? AccessCode { get; set; }
        [JsonPropertyName("reference")]         public string? Reference { get; set; }
    }
}

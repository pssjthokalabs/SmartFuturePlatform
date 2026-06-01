using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Paystack;

namespace SmartFuture.Infrastructure.Payments.Paystack;

/// <summary>
/// Phase 53 — initiates a Paystack transaction for an OrderIntent
/// WITHOUT requiring a real Order / Invoice / Payment. Interface is in
/// Application; implementation here owns the HTTP transport + the
/// UAT live-amount override gate.
///
/// Reference pattern: <c>SF-INTENT-{12-hex}</c>. The notify handler
/// looks at the prefix to route into the convert-to-order code path
/// instead of the PaymentInitiation lookup used for invoice-bound
/// payments.
/// </summary>
public class PaystackIntentInitiationService : IPaystackIntentInitiationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly PaystackSettings _settings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PaystackIntentInitiationService> _logger;

    public PaystackIntentInitiationService(
        HttpClient httpClient,
        IOptions<PaystackSettings> settings,
        IHostEnvironment env,
        ILogger<PaystackIntentInitiationService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _env = env;
        _logger = logger;
    }

    public async Task<PaystackIntentInitiationResult> InitiateAsync(
        PaystackIntentInitiationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || !_settings.IsConfigured)
            return PaystackIntentInitiationResult.Fail("Paystack is not enabled / configured.");

        if (string.IsNullOrWhiteSpace(request.CustomerEmail))
            return PaystackIntentInitiationResult.Fail("Cannot start a Paystack intent without a customer email.");

        if (request.InvoiceAmountAtTime <= 0m)
            return PaystackIntentInitiationResult.Fail("Installation fee amount must be greater than zero.");

        var liveKey = _settings.IsTestKey == false;
        var liveOverrideGate = !liveKey || _settings.AllowLiveTestAmountOverride;
        var overrideActive = _settings.UseTestAmountOverride
                          && _settings.TestAmount is > 0m
                          && !_env.IsProduction()
                          && liveOverrideGate;

        var amountSent = overrideActive ? _settings.TestAmount!.Value : request.InvoiceAmountAtTime;
        var amountSubunits = (long)Math.Round(amountSent * 100m, MidpointRounding.AwayFromZero);
        if (amountSubunits <= 0)
            return PaystackIntentInitiationResult.Fail("Paystack requires a positive amount in subunits.");

        var reference = BuildIntentReference();
        var currency = string.IsNullOrWhiteSpace(_settings.Currency)
            ? "ZAR"
            : _settings.Currency.Trim().ToUpperInvariant();

        var callbackUrl = string.IsNullOrWhiteSpace(request.CallbackUrl)
            ? _settings.CallbackUrl
            : request.CallbackUrl;
        var cancelUrl = string.IsNullOrWhiteSpace(request.CancelUrl)
            ? _settings.CancelUrl
            : request.CancelUrl;

        var body = new PaystackInitializeRequest
        {
            Email      = request.CustomerEmail.Trim(),
            Amount     = amountSubunits,
            Currency   = currency,
            Reference  = reference,
            CallbackUrl = string.IsNullOrWhiteSpace(callbackUrl) ? null : callbackUrl,
            Channels   = _settings.AllowedChannels is { Length: > 0 } chs ? chs : null,
            Metadata   = new
            {
                source        = "OrderIntent",
                orderIntentId = request.OrderIntentId,
                cancel_action = string.IsNullOrWhiteSpace(cancelUrl) ? null : cancelUrl,
            }
        };

        if (overrideActive)
        {
            _logger.LogWarning(
                "[PaystackLiveUatOverride] source=OrderIntent intentId={IntentId} invoiceAmount={InvoiceAmount} sentAmount={SentAmount} env={Env} liveKey={LiveKey}",
                request.OrderIntentId, request.InvoiceAmountAtTime, amountSent, _env.EnvironmentName, liveKey);
        }

        _logger.LogInformation(
            "[PaystackIntentInitiate] reference={Reference} intentId={IntentId} amountSubunits={Subunits} currency={Currency} email={Email} callbackUrl={CallbackUrl} overrideApplied={Override}",
            reference, request.OrderIntentId, amountSubunits, currency, request.CustomerEmail,
            callbackUrl ?? "(default)", overrideActive);

        using var message = new HttpRequestMessage(HttpMethod.Post, _settings.InitializeUrl)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SecretKey);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            PaystackInitializeResponse? parsed = null;
            try { parsed = JsonSerializer.Deserialize<PaystackInitializeResponse>(raw, JsonOptions); }
            catch { /* not JSON */ }

            if (!response.IsSuccessStatusCode || parsed?.Status != true || string.IsNullOrWhiteSpace(parsed.Data?.AuthorizationUrl))
            {
                _logger.LogWarning(
                    "[PaystackIntentInitiate] rejected — http={Status} body='{Body}'",
                    (int)response.StatusCode, Truncate(raw, 500));
                return PaystackIntentInitiationResult.Fail(
                    parsed?.Message
                    ?? $"Paystack rejected the initialize request (HTTP {(int)response.StatusCode}).");
            }

            return new PaystackIntentInitiationResult
            {
                Success                     = true,
                Reference                   = reference,
                AccessCode                  = parsed.Data?.AccessCode,
                RedirectUrl                 = parsed.Data!.AuthorizationUrl,
                AmountSent                  = amountSent,
                InvoiceAmountAtTime         = request.InvoiceAmountAtTime,
                IsTestAmountOverrideApplied = overrideActive,
                Currency                    = currency,
                AmountSubunits              = amountSubunits,
                PublicKey                   = _settings.PublicKey,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PaystackIntentInitiate] transport-error for {Reference}: {Message}",
                reference, ex.Message);
            return PaystackIntentInitiationResult.Fail($"We couldn't reach Paystack ({ex.GetType().Name}).");
        }
    }

    private static string BuildIntentReference()
    {
        var short12 = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        return $"SF-INTENT-{short12}";
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);

    private class PaystackInitializeRequest
    {
        [JsonPropertyName("email")]        public string Email { get; set; } = string.Empty;
        [JsonPropertyName("amount")]       public long Amount { get; set; }
        [JsonPropertyName("currency")]     public string Currency { get; set; } = "ZAR";
        [JsonPropertyName("reference")]    public string Reference { get; set; } = string.Empty;
        [JsonPropertyName("callback_url")] public string? CallbackUrl { get; set; }
        [JsonPropertyName("channels")]     public string[]? Channels { get; set; }
        [JsonPropertyName("metadata")]     public object? Metadata { get; set; }
    }

    private class PaystackInitializeResponse
    {
        [JsonPropertyName("status")]  public bool? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")]    public PaystackInitializeData? Data { get; set; }
    }

    private class PaystackInitializeData
    {
        [JsonPropertyName("authorization_url")] public string? AuthorizationUrl { get; set; }
        [JsonPropertyName("access_code")]       public string? AccessCode { get; set; }
        [JsonPropertyName("reference")]         public string? Reference { get; set; }
    }
}

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Shared.Results;
using SmartFuture.Shared.Errors;

namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Server-side helper that calls Paystack's
/// <c>GET /transaction/verify/{reference}</c> endpoint.
///
/// Used by:
///   - <see cref="PaystackNotifyHandler"/> as an optional belt-and-braces
///     confirmation before applying a status change.
///   - The customer / admin "Check status" path on the portal +
///     mobile pay screens, when the webhook hasn't landed yet.
///
/// The secret key is read from <see cref="PaystackSettings"/>; the
/// caller never has to thread it through. Verification is idempotent —
/// Paystack returns the same payload every time.
/// </summary>
public class PaystackVerificationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly PaystackSettings _settings;
    private readonly ILogger<PaystackVerificationService> _logger;

    public PaystackVerificationService(
        HttpClient httpClient,
        IOptions<PaystackSettings> settings,
        ILogger<PaystackVerificationService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Fetches Paystack's view of a transaction and returns a flat,
    /// SmartFuture-friendly DTO. Failures (transport, HTTP, or
    /// <c>status=false</c>) return a Failure result with a clear
    /// message — never null.
    /// </summary>
    public async Task<Result<PaystackVerifyOutcome>> VerifyAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
            return Result<PaystackVerifyOutcome>.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED, "Paystack is not configured.");

        if (string.IsNullOrWhiteSpace(reference))
            return Result<PaystackVerifyOutcome>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Reference is required.");

        var url = $"{_settings.VerifyBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(reference)}";

        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SecretKey);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            PaystackVerifyResponse? parsed = null;
            try { parsed = JsonSerializer.Deserialize<PaystackVerifyResponse>(rawBody, JsonOptions); }
            catch { /* leave null */ }

            _logger.LogInformation(
                "[PaystackVerifyDebug] httpStatus={StatusCode} reference={Reference} paystackStatus={PaystackStatus} " +
                "transactionStatus={TxnStatus} amountSubunits={Amount} currency={Currency} message='{Message}'",
                statusCode, reference,
                parsed?.Status?.ToString() ?? "(none)",
                parsed?.Data?.Status ?? "(none)",
                parsed?.Data?.Amount?.ToString() ?? "(none)",
                parsed?.Data?.Currency ?? "(none)",
                parsed?.Message ?? "(none)");

            if (!response.IsSuccessStatusCode || parsed is null || parsed.Status != true || parsed.Data is null)
            {
                return Result<PaystackVerifyOutcome>.Failure(
                    ErrorCodes.UPSTREAM_UNAVAILABLE,
                    parsed?.Message ?? $"Paystack verify call failed (HTTP {statusCode}).");
            }

            var outcome = new PaystackVerifyOutcome(
                Reference: parsed.Data.Reference ?? reference,
                Status: parsed.Data.Status ?? string.Empty,
                AmountSubunits: parsed.Data.Amount ?? 0,
                Currency: (parsed.Data.Currency ?? string.Empty).ToUpperInvariant(),
                GatewayResponse: parsed.Data.GatewayResponse,
                CustomerEmail: parsed.Data.Customer?.Email,
                PaidAtUtc: parsed.Data.PaidAt,
                ProviderTransactionId: parsed.Data.Id?.ToString());
            return Result<PaystackVerifyOutcome>.Success(outcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PaystackVerifyDebug] transport-error for {Reference}: {Message}", reference, ex.Message);
            return Result<PaystackVerifyOutcome>.Failure(
                ErrorCodes.UPSTREAM_UNAVAILABLE, $"We couldn't reach Paystack ({ex.GetType().Name}).");
        }
    }

    // ─── wire DTOs (private) ──────────────────────────────────────────

    private class PaystackVerifyResponse
    {
        [JsonPropertyName("status")]  public bool? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")]    public PaystackVerifyData? Data { get; set; }
    }

    private class PaystackVerifyData
    {
        [JsonPropertyName("id")]               public long? Id { get; set; }
        [JsonPropertyName("status")]           public string? Status { get; set; }
        [JsonPropertyName("reference")]        public string? Reference { get; set; }
        [JsonPropertyName("amount")]           public long? Amount { get; set; }
        [JsonPropertyName("currency")]         public string? Currency { get; set; }
        [JsonPropertyName("gateway_response")] public string? GatewayResponse { get; set; }
        [JsonPropertyName("paid_at")]          public DateTime? PaidAt { get; set; }
        [JsonPropertyName("customer")]         public PaystackVerifyCustomer? Customer { get; set; }
    }

    private class PaystackVerifyCustomer
    {
        [JsonPropertyName("email")] public string? Email { get; set; }
    }
}

/// <summary>
/// Flat verification outcome returned to SmartFuture handlers.
/// Amounts are in subunits (kobo/cents), matching Paystack's wire
/// representation; callers convert via /100 only where they need to
/// display.
/// </summary>
public sealed record PaystackVerifyOutcome(
    string Reference,
    string Status,
    long AmountSubunits,
    string Currency,
    string? GatewayResponse,
    string? CustomerEmail,
    DateTime? PaidAtUtc,
    string? ProviderTransactionId);

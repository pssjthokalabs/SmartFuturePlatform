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

            // Authorization block — present when Paystack stored a
            // reusable authorization for this charge. Forwarded to the
            // mandate-upsert path so verify-and-apply can capture the
            // same reusable authorization the webhook would have
            // captured. Never logged: only Last4/CardType ever leave
            // this method via the snapshot below.
            PaystackVerifyAuthorizationSnapshot? authSnapshot = null;
            if (parsed.Data.Authorization is not null
                && !string.IsNullOrWhiteSpace(parsed.Data.Authorization.AuthorizationCode))
            {
                authSnapshot = new PaystackVerifyAuthorizationSnapshot(
                    AuthorizationCode:    parsed.Data.Authorization.AuthorizationCode!,
                    Reusable:             parsed.Data.Authorization.Reusable ?? false,
                    Signature:            parsed.Data.Authorization.Signature,
                    Channel:              parsed.Data.Authorization.Channel,
                    CardType:             parsed.Data.Authorization.CardType,
                    Bank:                 parsed.Data.Authorization.Bank,
                    Last4:                parsed.Data.Authorization.Last4,
                    ExpMonth:             parsed.Data.Authorization.ExpMonth,
                    ExpYear:              parsed.Data.Authorization.ExpYear,
                    AccountName:          parsed.Data.Authorization.AccountName,
                    ProviderCustomerCode: parsed.Data.Customer?.CustomerCode);
            }

            var outcome = new PaystackVerifyOutcome(
                Reference: parsed.Data.Reference ?? reference,
                Status: parsed.Data.Status ?? string.Empty,
                AmountSubunits: parsed.Data.Amount ?? 0,
                Currency: (parsed.Data.Currency ?? string.Empty).ToUpperInvariant(),
                GatewayResponse: parsed.Data.GatewayResponse,
                CustomerEmail: parsed.Data.Customer?.Email,
                PaidAtUtc: parsed.Data.PaidAt,
                ProviderTransactionId: parsed.Data.Id?.ToString(),
                Authorization: authSnapshot);
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
        [JsonPropertyName("authorization")]    public PaystackVerifyAuthorization? Authorization { get; set; }
    }

    private class PaystackVerifyCustomer
    {
        [JsonPropertyName("email")]         public string? Email { get; set; }
        [JsonPropertyName("customer_code")] public string? CustomerCode { get; set; }
    }

    // Paystack's authorization object on /transaction/verify. Same
    // shape as the webhook charge.success authorization block — kept
    // private here so this assembly never exposes the raw
    // authorization_code outside the verify call.
    private class PaystackVerifyAuthorization
    {
        [JsonPropertyName("authorization_code")] public string? AuthorizationCode { get; set; }
        [JsonPropertyName("reusable")]           public bool? Reusable { get; set; }
        [JsonPropertyName("signature")]          public string? Signature { get; set; }
        [JsonPropertyName("channel")]            public string? Channel { get; set; }
        [JsonPropertyName("card_type")]          public string? CardType { get; set; }
        [JsonPropertyName("bank")]               public string? Bank { get; set; }
        [JsonPropertyName("last4")]              public string? Last4 { get; set; }
        [JsonPropertyName("exp_month")]          public string? ExpMonth { get; set; }
        [JsonPropertyName("exp_year")]           public string? ExpYear { get; set; }
        [JsonPropertyName("account_name")]       public string? AccountName { get; set; }
    }
}

/// <summary>
/// Verify-time reusable-authorization snapshot. Forwarded to the
/// mandate-upsert path so verify-and-apply can save the same reusable
/// authorization the webhook would have saved. Never serialized to
/// REST — internal cross-service contract only.
/// </summary>
public sealed record PaystackVerifyAuthorizationSnapshot(
    string AuthorizationCode,
    bool Reusable,
    string? Signature,
    string? Channel,
    string? CardType,
    string? Bank,
    string? Last4,
    string? ExpMonth,
    string? ExpYear,
    string? AccountName,
    string? ProviderCustomerCode);

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
    string? ProviderTransactionId,
    PaystackVerifyAuthorizationSnapshot? Authorization = null);

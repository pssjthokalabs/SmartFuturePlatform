using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Phase 7 — server-side helper that calls Paystack's
/// <c>POST /transaction/charge_authorization</c> using a stored
/// reusable authorization. Used by the auto-debit job (Phase 5/6)
/// to charge a customer's monthly service fee without re-prompting.
///
/// SAFETY GATE
/// -----------
/// Every public method short-circuits when
/// <c>AutoBilling__ChargeAuthorizationEnabled=false</c>. The class
/// is still resolvable from DI so callers (the install hook, retry
/// job, admin retry button) can wire against it now; flipping the
/// flag is the only step needed to turn real charges on.
///
/// The mandate's authorization code is unprotected only for the
/// lifetime of the HTTP call. It is NEVER logged, NEVER returned in a
/// response, and NEVER stored back in plaintext.
/// </summary>
public class PaystackChargeAuthorizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly PaystackSettings _paystackSettings;
    private readonly AutoBillingSettings _autoBilling;
    private readonly IAppDbContext _dbContext;
    private readonly IMandateProtector _protector;
    private readonly PaystackVerificationService _verifier;
    private readonly ILogger<PaystackChargeAuthorizationService> _logger;

    public PaystackChargeAuthorizationService(
        HttpClient httpClient,
        IOptions<PaystackSettings> paystackSettings,
        IOptions<AutoBillingSettings> autoBillingSettings,
        IAppDbContext dbContext,
        IMandateProtector protector,
        PaystackVerificationService verifier,
        ILogger<PaystackChargeAuthorizationService> logger)
    {
        _httpClient = httpClient;
        _paystackSettings = paystackSettings.Value;
        _autoBilling = autoBillingSettings.Value;
        _dbContext = dbContext;
        _protector = protector;
        _verifier = verifier;
        _logger = logger;
    }

    /// <summary>
    /// Generates a SmartFuture-namespaced reference for an auto-debit
    /// charge. Format: <c>SF-AUTO-YYYYMMDD-XXXXXX</c>. The 6-char
    /// suffix uses RandomNumberGenerator so concurrent worker threads
    /// can't collide on the same tick. Paystack-safe charset.
    /// </summary>
    public static string BuildAutoChargeReference()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var date = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        Span<byte> buffer = stackalloc byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
        var suffix = new char[6];
        for (var i = 0; i < 6; i++) suffix[i] = alphabet[buffer[i] % alphabet.Length];
        return $"SF-AUTO-{date}-{new string(suffix)}";
    }

    /// <summary>
    /// Issue a charge against the supplied mandate. Reference must
    /// be a SmartFuture-generated PaymentInitiation ProviderReference
    /// — the webhook handler / verification service rely on matching
    /// it back later.
    /// </summary>
    public async Task<Result<PaystackChargeOutcome>> ChargeAsync(
        Guid mandateId,
        decimal amountZar,
        string reference,
        CancellationToken cancellationToken = default)
    {
        if (!_autoBilling.Enabled)
            return Result<PaystackChargeOutcome>.Failure(
                ErrorCodes.SERVICE_UNAVAILABLE, "Auto-billing is disabled.");
        if (!_autoBilling.ChargeAuthorizationEnabled)
            return Result<PaystackChargeOutcome>.Failure(
                ErrorCodes.SERVICE_UNAVAILABLE,
                "Paystack charge-authorization calls are disabled (AutoBilling__ChargeAuthorizationEnabled=false).");
        if (!_paystackSettings.IsConfigured)
            return Result<PaystackChargeOutcome>.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED, "Paystack is not configured.");
        if (amountZar <= 0m)
            return Result<PaystackChargeOutcome>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(reference))
            return Result<PaystackChargeOutcome>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Reference is required.");

        var mandate = await _dbContext.CustomerPaymentMandates
            .FirstOrDefaultAsync(m => m.Id == mandateId, cancellationToken);
        if (mandate is null)
            return Result<PaystackChargeOutcome>.Failure(ErrorCodes.NOT_FOUND, "Mandate not found.");
        if (!mandate.IsActive || !mandate.IsReusable)
            return Result<PaystackChargeOutcome>.Failure(
                ErrorCodes.CONFLICT, "Mandate is not active or not reusable.");
        if (string.IsNullOrWhiteSpace(mandate.CustomerEmail))
            return Result<PaystackChargeOutcome>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Mandate has no email on file.");

        string authorizationCode;
        try
        {
            authorizationCode = _protector.Unprotect(mandate.AuthorizationCodeProtected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PaystackCharge] could not unprotect mandate {MandateId}", mandate.Id);
            return Result<PaystackChargeOutcome>.Failure(
                ErrorCodes.EXCEPTION, "Stored mandate is unreadable; customer must re-authorize.");
        }

        var subunits = (long)Math.Round(amountZar * 100m, MidpointRounding.AwayFromZero);
        var endpoint = "https://api.paystack.co/transaction/charge_authorization";

        var body = new PaystackChargeAuthRequest
        {
            AuthorizationCode = authorizationCode,
            Email = mandate.CustomerEmail!,
            Amount = subunits,
            Currency = string.IsNullOrWhiteSpace(_paystackSettings.Currency) ? "ZAR" : _paystackSettings.Currency,
            Reference = reference
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _paystackSettings.SecretKey);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        // [PaystackAutoCharge] structured outbound log. mandateId is
        // a SmartFuture-local Guid; reference is the SF-AUTO-… token
        // we just generated. Authorization code is NEVER logged.
        _logger.LogInformation(
            "[PaystackAutoCharge] outbound mandateId={MandateId} customerId={CustomerId} amountSubunits={Amount} currency={Currency} reference={Reference} endpoint={Endpoint}",
            mandate.Id, mandate.UserId, subunits, body.Currency, reference, endpoint);

        PaystackChargeResponse? parsed = null;
        int statusCode;
        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            statusCode = (int)response.StatusCode;

            try { parsed = JsonSerializer.Deserialize<PaystackChargeResponse>(rawBody, JsonOptions); }
            catch { /* leave null */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PaystackAutoCharge] transport-error mandateId={MandateId} reference={Reference}: {Message}",
                mandate.Id, reference, ex.Message);
            await ApplyChargeOutcomeToMandateAsync(mandate, success: false, cancellationToken);
            return Result<PaystackChargeOutcome>.Failure(
                ErrorCodes.UPSTREAM_UNAVAILABLE, $"We couldn't reach Paystack ({ex.GetType().Name}).");
        }

        var optimisticSuccess = statusCode >= 200 && statusCode < 300
                             && parsed?.Status == true
                             && string.Equals(parsed.Data?.Status, "success", StringComparison.OrdinalIgnoreCase);

        if (!optimisticSuccess)
        {
            _logger.LogWarning(
                "[PaystackAutoCharge] failure mandateId={MandateId} reference={Reference} httpStatus={Http} paystackStatus={PStatus} txStatus={Tx} message='{Msg}'",
                mandate.Id, reference, statusCode,
                parsed?.Status?.ToString() ?? "(none)",
                parsed?.Data?.Status ?? "(none)",
                parsed?.Message ?? "(none)");

            await ApplyChargeOutcomeToMandateAsync(mandate, success: false, cancellationToken);
            return Result<PaystackChargeOutcome>.Success(BuildOutcome(
                success: false, parsed, reference, subunits, body.Currency, statusCode));
        }

        // Belt-and-braces verification — same defence-in-depth pattern
        // the webhook handler uses. We've already had a 2xx + success
        // from charge_authorization; the verify call confirms the
        // amount / currency / status round-trip. A transport failure
        // on verify is treated as a soft skip (we trust the signed
        // charge response that just succeeded); a hard mismatch
        // (different amount/currency/status) demotes the charge to
        // failed.
        var verifyResult = await _verifier.VerifyAsync(reference, cancellationToken);
        if (verifyResult.IsSuccess)
        {
            var v = verifyResult.Data!;
            var verifyOk = string.Equals(v.Status, "success", StringComparison.OrdinalIgnoreCase)
                        && v.AmountSubunits == subunits
                        && (string.IsNullOrWhiteSpace(v.Currency)
                            || string.Equals(v.Currency, body.Currency, StringComparison.OrdinalIgnoreCase));
            if (!verifyOk)
            {
                _logger.LogWarning(
                    "[PaystackAutoCharge] verify-disagreement mandateId={MandateId} reference={Reference} verifyStatus={Status} verifyAmount={Amount} verifyCurrency={Currency} expectedAmount={ExpectedAmount} expectedCurrency={ExpectedCurrency}",
                    mandate.Id, reference, v.Status, v.AmountSubunits, v.Currency, subunits, body.Currency);
                await ApplyChargeOutcomeToMandateAsync(mandate, success: false, cancellationToken);
                return Result<PaystackChargeOutcome>.Success(BuildOutcome(
                    success: false, parsed, reference, subunits, body.Currency, statusCode,
                    overrideMessage: "Paystack verify-call disagreed with charge response."));
            }
        }
        else
        {
            _logger.LogWarning(
                "[PaystackAutoCharge] verify-call failed mandateId={MandateId} reference={Reference}: {Message} — proceeding on signed charge response only.",
                mandate.Id, reference, verifyResult.Message);
        }

        await ApplyChargeOutcomeToMandateAsync(mandate, success: true, cancellationToken);

        _logger.LogInformation(
            "[PaystackAutoCharge] success mandateId={MandateId} customerId={CustomerId} reference={Reference} amountSubunits={Amount} providerTransactionId={Tx}",
            mandate.Id, mandate.UserId, reference, subunits, parsed!.Data!.Id);

        return Result<PaystackChargeOutcome>.Success(BuildOutcome(
            success: true, parsed, reference, subunits, body.Currency, statusCode));
    }

    private static PaystackChargeOutcome BuildOutcome(
        bool success,
        PaystackChargeResponse? parsed,
        string reference,
        long subunits,
        string currency,
        int httpStatusCode,
        string? overrideMessage = null) => new(
        Success: success,
        Reference: parsed?.Data?.Reference ?? reference,
        AmountSubunits: parsed?.Data?.Amount ?? subunits,
        Currency: parsed?.Data?.Currency ?? currency,
        Status: parsed?.Data?.Status ?? string.Empty,
        GatewayMessage: overrideMessage ?? parsed?.Message,
        ProviderTransactionId: parsed?.Data?.Id?.ToString(CultureInfo.InvariantCulture),
        PaidAtUtc: parsed?.Data?.PaidAt,
        HttpStatusCode: httpStatusCode);

    // Updates the mandate's success/failure counters atomically with
    // the SaveChanges call. Authorization code is untouched.
    private async Task ApplyChargeOutcomeToMandateAsync(
        Domain.Billing.CustomerPaymentMandate mandate,
        bool success,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (success)
        {
            mandate.LastSuccessfulChargeUtc = now;
            mandate.ConsecutiveFailureCount = 0;
        }
        else
        {
            mandate.LastFailedChargeUtc = now;
            mandate.ConsecutiveFailureCount += 1;
        }
        mandate.UpdatedAtUtc = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private class PaystackChargeAuthRequest
    {
        [JsonPropertyName("authorization_code")] public string AuthorizationCode { get; set; } = string.Empty;
        [JsonPropertyName("email")]              public string Email { get; set; } = string.Empty;
        [JsonPropertyName("amount")]             public long Amount { get; set; }
        [JsonPropertyName("currency")]           public string Currency { get; set; } = "ZAR";
        [JsonPropertyName("reference")]          public string Reference { get; set; } = string.Empty;
    }

    private class PaystackChargeResponse
    {
        [JsonPropertyName("status")]  public bool? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")]    public PaystackChargeData? Data { get; set; }
    }

    private class PaystackChargeData
    {
        [JsonPropertyName("id")]        public long? Id { get; set; }
        [JsonPropertyName("status")]    public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("amount")]    public long? Amount { get; set; }
        [JsonPropertyName("currency")]  public string? Currency { get; set; }
        [JsonPropertyName("paid_at")]   public DateTime? PaidAt { get; set; }
    }
}

public sealed record PaystackChargeOutcome(
    bool Success,
    string Reference,
    long AmountSubunits,
    string Currency,
    string Status,
    string? GatewayMessage,
    string? ProviderTransactionId,
    DateTime? PaidAtUtc,
    int HttpStatusCode);

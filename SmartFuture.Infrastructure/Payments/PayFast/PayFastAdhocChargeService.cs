using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.PayFast;

namespace SmartFuture.Infrastructure.Payments.PayFast;

/// <summary>
/// Phase 1B — HttpClient implementation of the PayFast ad-hoc charge API
/// (<c>POST /subscriptions/{token}/adhoc</c>). Signs with the PayFast API
/// signature (alphabetical sort + passphrase + MD5), POSTs the charge, and
/// returns a transport-level result.
///
/// SECURITY: the token appears only in the request URL/path and is NEVER
/// logged — logs use a masked endpoint, the reference, amount, and HTTP
/// status only. Passphrase + signature are never logged.
/// </summary>
public sealed class PayFastAdhocChargeService : IPayFastAdhocChargeService
{
    private readonly HttpClient _httpClient;
    private readonly PayFastSettings _settings;
    private readonly ILogger<PayFastAdhocChargeService> _logger;

    public PayFastAdhocChargeService(
        HttpClient httpClient,
        IOptions<PayFastSettings> settings,
        ILogger<PayFastAdhocChargeService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<PayFastAdhocChargeResult> ChargeAsync(
        string token, long amountCents, string itemName, string mPaymentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return PayFastAdhocChargeResult.Transport("Missing PayFast token.");
        if (amountCents <= 0)
            return PayFastAdhocChargeResult.Transport("Amount must be greater than zero.");

        // ISO-8601 timestamp. NOTE: re-verify PayFast's required timezone/
        // format in UAT (sandbox accepts ISO-8601; offset shown as +00:00).
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

        // Header + body params that participate in the API signature.
        // `testing` is a query param only and is NOT signed.
        var signedParams = new List<KeyValuePair<string, string>>
        {
            new("merchant-id", _settings.MerchantId),
            new("version",     _settings.ApiVersion),
            new("timestamp",   timestamp),
            new("amount",      amountCents.ToString(CultureInfo.InvariantCulture)),
            new("item_name",   itemName),
            new("m_payment_id", mPaymentId),
        };

        var signature = PayFastSignatureCalculator.GenerateApiSignature(signedParams, _settings.Passphrase);

        var baseUrl = _settings.ApiBaseUrl.TrimEnd('/');
        // Token is in the path — build the real URL but NEVER log it.
        var url = $"{baseUrl}/subscriptions/{Uri.EscapeDataString(token)}/adhoc";
        if (_settings.EffectiveUseSandboxApi)
            url += "?testing=true";

        var maskedEndpoint = $"{baseUrl}/subscriptions/***/adhoc{(_settings.EffectiveUseSandboxApi ? "?testing=true" : string.Empty)}";

        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        message.Headers.TryAddWithoutValidation("merchant-id", _settings.MerchantId);
        message.Headers.TryAddWithoutValidation("version", _settings.ApiVersion);
        message.Headers.TryAddWithoutValidation("timestamp", timestamp);
        message.Headers.TryAddWithoutValidation("signature", signature);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        // Body params (the signed body fields, minus the header trio).
        message.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["amount"] = amountCents.ToString(CultureInfo.InvariantCulture),
            ["item_name"] = itemName,
            ["m_payment_id"] = mPaymentId,
        });

        // Outbound log — token NEVER included (masked endpoint only).
        _logger.LogInformation(
            "[payment][payfast][adhoc] outbound reference={Reference} amountCents={AmountCents} endpoint={Endpoint} merchantId={MerchantIdMasked} sandbox={Sandbox}",
            mPaymentId, amountCents, maskedEndpoint, MaskId(_settings.MerchantId), _settings.EffectiveUseSandboxApi);

        int statusCode;
        string rawBody;
        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            statusCode = (int)response.StatusCode;
            rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[payment][payfast][adhoc] transport-error reference={Reference}: {Message}",
                mPaymentId, ex.Message);
            return PayFastAdhocChargeResult.Transport($"We couldn't reach PayFast ({ex.GetType().Name}).");
        }

        var providerStatus = TryExtractStatus(rawBody);
        var accepted = statusCode is >= 200 and < 300;
        var snippet = Snippet(rawBody);

        if (accepted)
        {
            _logger.LogInformation(
                "[payment][payfast][adhoc] accepted reference={Reference} httpStatus={Http} providerStatus={ProviderStatus}",
                mPaymentId, statusCode, providerStatus ?? "(none)");
            // No synchronous pf_payment_id is assumed — settlement (and the
            // pf id) arrive via the ITN. Caller maps Accepted → Pending.
            return PayFastAdhocChargeResult.AcceptedResult(statusCode, providerTransactionId: null, providerStatus, snippet);
        }

        _logger.LogWarning(
            "[payment][payfast][adhoc] rejected reference={Reference} httpStatus={Http} providerStatus={ProviderStatus} message='{Message}'",
            mPaymentId, statusCode, providerStatus ?? "(none)", snippet ?? "(none)");
        return PayFastAdhocChargeResult.Rejected(statusCode, providerStatus, snippet);
    }

    private static string? TryExtractStatus(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var s)
                && s.ValueKind == JsonValueKind.String)
                return s.GetString();
        }
        catch { /* non-JSON or unexpected shape — leave null */ }
        return null;
    }

    private static string? Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var trimmed = body.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300];
    }

    private static string MaskId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "(empty)";
        if (id.Length <= 4) return new string('*', id.Length);
        return $"{id[..2]}***{id[^2..]}";
    }
}

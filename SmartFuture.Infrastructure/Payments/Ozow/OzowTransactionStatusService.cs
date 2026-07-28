using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Ozow;

namespace SmartFuture.Infrastructure.Payments.Ozow;

/// <summary>
/// Calls Ozow's <c>GetTransactionByReference</c> endpoint. See
/// <see cref="IOzowTransactionStatusService"/> for why this exists.
///
/// Ozow returns a JSON ARRAY of transaction records for a reference
/// (a reference can in principle be retried), so we take the most
/// decisive record: a Complete beats anything else, otherwise the first.
/// </summary>
public class OzowTransactionStatusService : IOzowTransactionStatusService
{
    private const string LiveStatusUrl = "https://api.ozow.com/GetTransactionByReference";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OzowSettings _settings;
    private readonly ILogger<OzowTransactionStatusService> _logger;

    public OzowTransactionStatusService(
        HttpClient httpClient,
        IOptions<OzowSettings> settings,
        ILogger<OzowTransactionStatusService> logger)
    {
        _httpClient = httpClient;
        _settings   = settings.Value;
        _logger     = logger;
    }

    public async Task<OzowTransactionStatusResult> GetByReferenceAsync(
        string transactionReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionReference))
            return OzowTransactionStatusResult.Fail("transactionReference is required.");

        if (!_settings.IsConfigured)
            return OzowTransactionStatusResult.Fail("Ozow is not configured.");

        var isTest = _settings.IsTest!.Value;
        var url = $"{LiveStatusUrl}" +
                  $"?siteCode={Uri.EscapeDataString(_settings.SiteCode)}" +
                  $"&transactionReference={Uri.EscapeDataString(transactionReference)}" +
                  $"&IsTest={(isTest ? "true" : "false")}";

        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.TryAddWithoutValidation("ApiKey", _settings.ApiKey);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var status  = (int)response.StatusCode;

            _logger.LogInformation(
                "[OzowStatusCheck] reference={Reference} httpStatus={StatusCode} isTest={IsTest} rawBody='{RawBody}'",
                transactionReference, status, isTest, Truncate(rawBody, 1000));

            if (!response.IsSuccessStatusCode)
            {
                return new OzowTransactionStatusResult
                {
                    Success       = false,
                    FailureReason = $"Ozow status check failed (HTTP {status}).",
                    RawSnippet    = Truncate(rawBody, 500)
                };
            }

            var records = ParseRecords(rawBody);
            if (records.Count == 0)
            {
                // Reached Ozow; it has no record. Definitive answer.
                return new OzowTransactionStatusResult
                {
                    Success    = true,
                    Found      = false,
                    RawSnippet = Truncate(rawBody, 500)
                };
            }

            // Prefer a Complete record if one exists.
            var chosen = records.FirstOrDefault(r =>
                             string.Equals(r.Status, "Complete", StringComparison.OrdinalIgnoreCase))
                         ?? records[0];

            return new OzowTransactionStatusResult
            {
                Success       = true,
                Found         = true,
                Status        = chosen.Status,
                TransactionId = chosen.TransactionId,
                Amount        = chosen.AmountValue,
                IsTest        = chosen.IsTest,
                StatusMessage = chosen.StatusMessage,
                RawSnippet    = Truncate(rawBody, 500)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[OzowStatusCheck] transport-error reference={Reference}: {Message}",
                transactionReference, ex.Message);
            return OzowTransactionStatusResult.Fail($"We couldn't reach Ozow ({ex.GetType().Name}).");
        }
    }

    // Ozow returns an array; some error shapes return a single object.
    // Parse defensively so a shape surprise degrades to "not found"
    // rather than throwing inside a recovery tool.
    private static List<OzowStatusRecord> ParseRecords(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return new List<OzowStatusRecord>();

        try
        {
            var trimmed = rawBody.TrimStart();
            if (trimmed.StartsWith('['))
            {
                return JsonSerializer.Deserialize<List<OzowStatusRecord>>(rawBody, JsonOptions)
                       ?? new List<OzowStatusRecord>();
            }

            var single = JsonSerializer.Deserialize<OzowStatusRecord>(rawBody, JsonOptions);
            return single is null || string.IsNullOrWhiteSpace(single.Status)
                ? new List<OzowStatusRecord>()
                : new List<OzowStatusRecord> { single };
        }
        catch
        {
            return new List<OzowStatusRecord>();
        }
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);

    private class OzowStatusRecord
    {
        public string? TransactionId { get; set; }
        public string? TransactionReference { get; set; }
        public string? Status { get; set; }
        public string? StatusMessage { get; set; }
        public bool? IsTest { get; set; }

        // Ozow has shipped Amount as both a number and a string across
        // their endpoints. Accept either.
        public JsonElement Amount { get; set; }

        public decimal? AmountValue =>
            Amount.ValueKind switch
            {
                JsonValueKind.Number => Amount.TryGetDecimal(out var d) ? d : null,
                JsonValueKind.String => decimal.TryParse(Amount.GetString(), NumberStyles.Any,
                                            CultureInfo.InvariantCulture, out var s) ? s : null,
                _ => null
            };
    }
}

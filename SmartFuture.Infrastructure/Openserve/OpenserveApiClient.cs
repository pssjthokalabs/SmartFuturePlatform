using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;

namespace SmartFuture.Infrastructure.Openserve;

// Server-side-only HTTP client for the Openserve Fulfilment API. Paths,
// headers and payload shapes follow Openserve's provisioned Postman
// collection "Fulfilment API Collection (Broadband)" v02 — the executable
// contract for Smart Future's staging tenant. Where it disagrees with the
// PDF (ITSD-179559 Rev 04.002) the collection wins:
//   • Query Order Details is GET /{isp_tag}/getproductorder/{id}
//     (PDF §4.6.1 says /productorder/{id}).
//   • Create Order is POST /{isp_tag}/productorder, all lowercase
//     (PDF examples use camelCase /productOrder).
//   • FromLocation differs per operation: {isp_tag} on Product
//     Qualification, {ISPID} on every Product Ordering request.
//   • ReplyToAddress is "Callback URL that will be provided by Openserve"
//     — an Openserve-supplied value sent verbatim, not a Smart Future URL.
//
// Never called directly from a controller — OpenserveOrderSubmissionService,
// OpenserveQualificationService, OpenserveReconciliationService and the
// admin diagnostics service are the only callers, and each persists the
// sanitized request (api_key redacted) plus the raw response into
// OpenserveIntegrationLog for support.
//
// BaseUrl is read per-call from IOpenserveRuntimeConfigProvider (DB
// override merged with appsettings/env fallback — see Admin →
// Integrations → Openserve) rather than baked into HttpClient.BaseAddress
// at DI registration time — OpenserveFulfilment is disabled (and BaseUrl
// often empty) by default, and constructing a System.Uri from an empty
// string at startup would throw before the host even finishes composing.
public class OpenserveApiClient : IOpenserveApiClient
{
    public const string ProductQualificationPath = "productqualification";
    public const string ProductOrderPath = "productorder";
    public const string GetProductOrderPath = "getproductorder";
    public const string CancelProductOrderPath = "cancelproductorder";

    public const string MessageIdHeader = "MessageID";
    public const string FromLocationHeader = "FromLocation";
    public const string SenderIdHeader = "SenderID";
    public const string ReplyToAddressHeader = "ReplyToAddress";
    public const string ApiKeyHeader = "api_key";

    private const int MinTimeoutSeconds = 5;
    private const int MaxTimeoutSeconds = 120;

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Which header set an operation sends — taken verbatim from the Postman collection, per request.</summary>
    private enum HeaderProfile
    {
        /// <summary>productQualification AMID / LatLong: MessageID, FromLocation={isp_tag}, SenderID, api_key. No ReplyToAddress.</summary>
        ProductQualification,

        /// <summary>Create / Query Order Details / Cancel Inflight: MessageID, FromLocation={ISPID}, SenderID, ReplyToAddress, api_key.</summary>
        ProductOrdering
    }

    private readonly HttpClient _httpClient;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly ILogger<OpenserveApiClient> _logger;

    public OpenserveApiClient(HttpClient httpClient, IOpenserveRuntimeConfigProvider configProvider, ILogger<OpenserveApiClient> logger)
    {
        _httpClient = httpClient;
        _configProvider = configProvider;
        _logger = logger;
    }

    public async Task<OpenserveApiCallResult<OpenserveCreateOrderOutcome>> CreateOrderAsync(OpenserveCreateOrderCommand command, CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        var endpoint = BuildUrl(settings, ProductOrderPath);

        var request = new OpenserveCreateOrderRequest
        {
            Type = "Sales Order",
            RequestedStartDate = (command.RequestedStartDateUtc ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss"),
            Comments = string.IsNullOrWhiteSpace(command.Comment)
                ? null
                : new List<OpenserveComment> { new() { Comment = command.Comment! } },
            ProductOrderItem = new List<OpenserveProductOrderItem>
            {
                new()
                {
                    Action = "add",
                    Product = new OpenserveOrderProduct
                    {
                        ProductOffering = new OpenserveProductOffering { Name = command.OpenserveProductName },
                        ProductCharacteristic = new List<OpenserveNameValue>
                        {
                            new("External Reference Number", command.ExternalReferenceNumber),
                            new("SKU", command.Sku)
                        },
                        ProductRelationship = new List<OpenserveProductRelationship>
                        {
                            new() { RelationshipType = "childOffer", Product = new { } }
                        },
                        RealizingService = new List<OpenserveRealizingService>
                        {
                            new()
                            {
                                Place = new List<OpenservePlace>
                                {
                                    new()
                                    {
                                        Type = "A",
                                        BuildingName = command.BuildingName ?? string.Empty,
                                        Unit = command.Unit ?? string.Empty,
                                        Floor = command.Floor ?? string.Empty,
                                        Street1 = command.Street1,
                                        Suburb = command.Suburb,
                                        City = command.City,
                                        Region = command.Region,
                                        Country = "South Africa",
                                        Longitude = command.Longitude,
                                        Latitude = command.Latitude,
                                        BuildingNumId = command.BuildingNumId ?? string.Empty,
                                        Amid = command.Amid
                                    }
                                },
                                ServiceCharacteristic = BuildServiceCharacteristics(command)
                            }
                        }
                    }
                }
            }
        };

        var requestJson = JsonSerializer.Serialize(request, RequestJsonOptions);

        return await SendAsync<OpenserveCreateOrderOutcome>(HttpMethod.Post, endpoint, HeaderProfile.ProductOrdering, requestJson,
            responseBody =>
            {
                var ack = JsonSerializer.Deserialize<OpenserveAckResponse>(responseBody, ResponseJsonOptions);
                var errorResult = ack?.ErrorResult;
                var isBusinessSuccess = errorResult is not null && errorResult.Code == "0";
                var (parsedOrderId, parsedState) = OpenserveOrderFactsExtractor.TryParseAckMessage(errorResult?.Message);

                // ErrorResult.Code is just the 0/1 success flag (spec
                // examples: "0" on ack, "1" on reject) — the actual
                // Openserve-specific error code (e.g. "GEN-45994",
                // "E84119") lives in Reason. Falls back to Code only if
                // Reason is somehow absent.
                var errorCode = errorResult?.Reason ?? errorResult?.Code;

                return (
                    isBusinessSuccess,
                    errorCode,
                    errorResult?.Message ?? "Openserve returned no ErrorResult.",
                    new OpenserveCreateOrderOutcome(parsedOrderId, parsedState, errorResult?.Message ?? string.Empty));
            },
            cancellationToken);
    }

    public async Task<OpenserveApiCallResult<OpenserveGetOrderOutcome>> GetOrderAsync(string openserveOrderId, CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        var endpoint = BuildUrl(settings, $"{GetProductOrderPath}/{Uri.EscapeDataString(openserveOrderId.Trim())}");

        return await SendAsync<OpenserveGetOrderOutcome>(HttpMethod.Get, endpoint, HeaderProfile.ProductOrdering, requestBodyJson: string.Empty,
            responseBody =>
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                // An unknown/foreign order id can come back as a 200 carrying
                // the ordering-API ErrorResult envelope instead of an order.
                if (TryReadEmbeddedError(root, out var embeddedCode, out var embeddedMessage))
                {
                    return (false, embeddedCode, embeddedMessage ?? "Openserve rejected the order lookup.", new OpenserveGetOrderOutcome(null, null, null, null, null));
                }

                var facts = OpenserveOrderFactsExtractor.Extract(root);
                return (true, null, "OK", new OpenserveGetOrderOutcome(facts.Id, facts.State, facts.OrderName, facts.ServiceOrderNumber, facts.OrderDate, facts.OrderType, facts.CircuitNumber));
            },
            cancellationToken);
    }

    public async Task<OpenserveApiCallResult<OpenserveCancelOrderOutcome>> CancelOrderAsync(string openserveOrderId, CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        var endpoint = BuildUrl(settings, CancelProductOrderPath);

        var request = new OpenserveCancelOrderRequest { ProductOrder = new OpenserveOrderRef { Id = openserveOrderId } };
        var requestJson = JsonSerializer.Serialize(request, RequestJsonOptions);

        return await SendAsync<OpenserveCancelOrderOutcome>(HttpMethod.Post, endpoint, HeaderProfile.ProductOrdering, requestJson,
            responseBody =>
            {
                var envelope = JsonSerializer.Deserialize<OpenserveResultEnvelope>(responseBody, ResponseJsonOptions);
                if (envelope?.Result is null)
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (TryReadEmbeddedError(doc.RootElement, out var embeddedCode, out var embeddedMessage))
                        return (false, embeddedCode, embeddedMessage ?? "Openserve rejected the cancellation.", new OpenserveCancelOrderOutcome(null, null, null));
                }

                var isSuccess = envelope?.Result?.ResultCode == "0";
                return (
                    isSuccess,
                    envelope?.Result?.ResultMsgCode ?? envelope?.Result?.ResultCode,
                    envelope?.Result?.ResultMsg ?? "Openserve returned no Result.",
                    new OpenserveCancelOrderOutcome(envelope?.Payload?.Id ?? envelope?.Payload?.Order?.Id, envelope?.Payload?.State, envelope?.Payload?.EffectiveCancellationDate));
            },
            cancellationToken);
    }

    public async Task<OpenserveApiCallResult<OpenserveQualificationOutcome>> QualifyAsync(OpenserveQualificationQuery query, CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;

        // Postman sends either ?AMID=..&BuildingInfo=Y or
        // ?LAT=..&LON=..&BuildingInfo=Y — never both. AMID wins when given.
        var queryParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Amid))
        {
            queryParams.Add($"AMID={Uri.EscapeDataString(query.Amid.Trim())}");
        }
        else
        {
            if (query.Latitude.HasValue)
                queryParams.Add($"LAT={query.Latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (query.Longitude.HasValue)
                queryParams.Add($"LON={query.Longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        queryParams.Add($"BuildingInfo={(query.BuildingInfo ? "Y" : "N")}");

        var endpoint = BuildUrl(settings, ProductQualificationPath) + "?" + string.Join("&", queryParams);

        return await SendAsync<OpenserveQualificationOutcome>(HttpMethod.Get, endpoint, HeaderProfile.ProductQualification, requestBodyJson: string.Empty,
            responseBody =>
            {
                var parsed = JsonSerializer.Deserialize<OpenserveQualificationResponse>(responseBody, ResponseJsonOptions);
                var isSuccess = parsed is not null && (parsed.ErrorCode is null or 0);
                var addressInfo = parsed?.Results?.Payload?.AddressInfo;
                var buildingRows = addressInfo?.buildingNumberInfo?.buildingInfo ?? new List<OpenserveQualificationBuildingInfo>();

                // Only auto-select a buildingNumId when exactly one
                // candidate came back — an MDU with multiple units/
                // buildings at the same AMID is disambiguated later
                // against the customer's own unit number (see
                // OpenserveBuildingMatcher), never guessed here.
                var buildingNumId = buildingRows.Count == 1 ? buildingRows[0].BLD_NUM_ID : null;
                var ftthInfo = parsed?.Results?.Payload?.FtthInfrastructure?.ftthInfo?.FirstOrDefault();

                var products = ftthInfo?.ftthProductInfo?
                    .Select(p => new OpenserveQualificationProduct(p.ProductName, p.ProductCode, p.upstreamSpeed, p.downstreamSpeed))
                    .ToList();

                var buildings = buildingRows
                    .Select(b => new OpenserveQualificationBuilding(b.AM_ID, b.BLD_NUM_ID, b.BLD_ID, b.FLOOR_ID, b.NUM, b.BUILDING_NAME, b.FLOOR))
                    .ToList();

                var outcome = new OpenserveQualificationOutcome(
                    addressInfo?.AMID, buildingNumId, buildingRows.Count, addressInfo?.LR_Address, ftthInfo?.FTTH_Status,
                    ftthInfo?.fibreMaxSpeed, ftthInfo?.fibreMaxSpeedUnit, addressInfo?.LR_SUBURB, addressInfo?.LR_TOWN, addressInfo?.LR_PROVINCE,
                    products, buildings);

                return (
                    isSuccess,
                    parsed?.ErrorCode?.ToString(),
                    parsed?.Message is { Length: > 0 } m ? m : parsed?.ErrorString ?? "Openserve returned no payload.",
                    outcome);
            },
            cancellationToken);
    }

    private static List<OpenserveNameValue> BuildServiceCharacteristics(OpenserveCreateOrderCommand command)
    {
        var list = new List<OpenserveNameValue>
        {
            new("Capacity", command.Capacity),
            new("Capacity UOM", command.CapacityUom)
        };

        if (!string.IsNullOrWhiteSpace(command.SubscriberReferenceNumber))
            list.Add(new OpenserveNameValue("Subscriber Reference Number", command.SubscriberReferenceNumber!));
        if (!string.IsNullOrWhiteSpace(command.SubscriberContactName))
            list.Add(new OpenserveNameValue("Subscriber Contact Name", command.SubscriberContactName!));
        if (!string.IsNullOrWhiteSpace(command.SubscriberContactPhone))
            list.Add(new OpenserveNameValue("Subscriber Contact Phone", command.SubscriberContactPhone!));

        list.Add(new OpenserveNameValue("ISP Identifier", command.IspIdentifier));
        return list;
    }

    /// <summary>
    /// The exact header set for one call, in the collection's own order.
    /// FromLocation is per-profile (isp_tag vs ISPID) and ReplyToAddress is
    /// only on Product Ordering — see <see cref="HeaderProfile"/>. Blank
    /// values are omitted rather than sent empty.
    /// </summary>
    private static List<KeyValuePair<string, string>> BuildHeaders(OpenserveFulfilmentSettings settings, string messageId, HeaderProfile profile)
    {
        var fromLocation = profile == HeaderProfile.ProductQualification ? settings.WsIspCode : settings.IspIdentifier;

        var headers = new List<KeyValuePair<string, string>> { new(MessageIdHeader, messageId) };
        AddIfPresent(headers, FromLocationHeader, fromLocation);
        AddIfPresent(headers, SenderIdHeader, settings.SenderId);
        if (profile == HeaderProfile.ProductOrdering) AddIfPresent(headers, ReplyToAddressHeader, settings.ReplyToAddress);
        AddIfPresent(headers, ApiKeyHeader, settings.ApiKey);
        return headers;
    }

    private static void AddIfPresent(List<KeyValuePair<string, string>> headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) headers.Add(new KeyValuePair<string, string>(name, value.Trim()));
    }

    private async Task<OpenserveApiCallResult<TOutcome>> SendAsync<TOutcome>(HttpMethod method, string endpoint, HeaderProfile profile, string requestBodyJson,
        Func<string, (bool IsSuccess, string? ErrorCode, string? ErrorMessage, TOutcome Outcome)> parseResponse, CancellationToken cancellationToken)
    {
        var settings = _configProvider.Current;
        var messageId = Guid.NewGuid().ToString();
        var headers = BuildHeaders(settings, messageId, profile);

        // Built from the same list that goes on the wire, with api_key
        // replaced — the plaintext key never reaches this string.
        var sanitizedHeadersJson = OpenserveHeaderRedaction.ToSanitizedJson(headers);

        using var httpRequest = new HttpRequestMessage(method, endpoint);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(name, value);
        }

        if (method != HttpMethod.Get)
        {
            httpRequest.Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");
        }

        // HttpTimeoutSeconds is admin-configurable; the HttpClient's own
        // timeout is only a generous ceiling (see ServiceExtensions).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.HttpTimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds)));

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                // Openserve's 4xx bodies carry the real reason (Postman
                // examples: 400 + ErrorResult{reason:"E84119", message:"Service
                // Number ... does not exist"}) — surface it instead of a bare status.
                var (bodyCode, bodyMessage) = TryReadErrorBody(responseBody);
                _logger.LogWarning("Openserve {Method} {Endpoint} returned HTTP {Status} ({Code}). messageId={MessageId}", method, endpoint, status, bodyCode, messageId);

                return OpenserveApiCallResult<TOutcome>.Failure(messageId, method.Method, endpoint, status, requestBodyJson, responseBody,
                    errorCode: bodyCode ?? response.StatusCode.ToString(),
                    errorMessage: bodyMessage is null ? $"Openserve returned HTTP {status}." : $"Openserve returned HTTP {status}: {bodyMessage}",
                    requestHeadersJson: sanitizedHeadersJson);
            }

            (bool isSuccess, string? errorCode, string? errorMessage, TOutcome outcome) parsed;
            try
            {
                parsed = parseResponse(responseBody);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Openserve {Method} {Endpoint} returned unparseable JSON. messageId={MessageId}", method, endpoint, messageId);
                return OpenserveApiCallResult<TOutcome>.Failure(messageId, method.Method, endpoint, status, requestBodyJson, responseBody,
                    errorCode: "PARSE_ERROR", errorMessage: "Openserve response could not be parsed.", requestHeadersJson: sanitizedHeadersJson);
            }

            if (!parsed.isSuccess)
            {
                return OpenserveApiCallResult<TOutcome>.Failure(messageId, method.Method, endpoint, status, requestBodyJson, responseBody,
                    parsed.errorCode, parsed.errorMessage ?? "Openserve rejected the request.", requestHeadersJson: sanitizedHeadersJson);
            }

            return OpenserveApiCallResult<TOutcome>.Success(messageId, method.Method, endpoint, status, requestBodyJson, responseBody, parsed.outcome, sanitizedHeadersJson);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Openserve {Method} {Endpoint} timed out. messageId={MessageId}", method, endpoint, messageId);
            return OpenserveApiCallResult<TOutcome>.Failure(messageId, method.Method, endpoint, null, requestBodyJson, null,
                errorCode: "TIMEOUT", errorMessage: "Openserve request timed out.", requestHeadersJson: sanitizedHeadersJson);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Openserve {Method} {Endpoint} transport failure. messageId={MessageId}", method, endpoint, messageId);
            return OpenserveApiCallResult<TOutcome>.Failure(messageId, method.Method, endpoint, null, requestBodyJson, null,
                errorCode: "TRANSPORT_ERROR", errorMessage: ex.Message, requestHeadersJson: sanitizedHeadersJson);
        }
    }

    /// <summary>Best-effort read of ErrorResult / Result from a non-2xx body. Non-JSON bodies (e.g. a gateway HTML page) yield a short plain-text snippet instead.</summary>
    private static (string? Code, string? Message) TryReadErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);

        try
        {
            var envelope = JsonSerializer.Deserialize<OpenserveErrorEnvelope>(body, ResponseJsonOptions);
            if (envelope?.ErrorResult is { } er && (er.Reason is not null || er.Message is not null))
                return (er.Reason ?? er.Code, er.Message);
            if (envelope?.Result is { } r && (r.ResultMsg is not null || r.ResultMsgCode is not null))
                return (r.ResultMsgCode ?? r.ResultCode, r.ResultMsg);
            return (null, null);
        }
        catch (JsonException)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", " ").Trim();
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            return (null, text.Length == 0 ? null : (text.Length > 200 ? text[..200] + "…" : text));
        }
    }

    private static bool TryReadEmbeddedError(JsonElement root, out string? code, out string? message)
    {
        code = null;
        message = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ErrorResult", out var er) || er.ValueKind != JsonValueKind.Object)
            return false;

        var flag = er.TryGetProperty("code", out var c) ? c.ToString() : null;
        if (flag == "0") return false;

        code = er.TryGetProperty("reason", out var reason) ? reason.ToString() : flag;
        message = er.TryGetProperty("message", out var msg) ? msg.ToString() : null;
        return true;
    }

    private static string BuildUrl(OpenserveFulfilmentSettings settings, string path)
        => $"{settings.BaseUrl.Trim().TrimEnd('/')}/{settings.WsIspCode.Trim().Trim('/')}/{path}";
}

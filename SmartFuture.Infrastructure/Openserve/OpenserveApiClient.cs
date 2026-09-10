using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;

namespace SmartFuture.Infrastructure.Openserve;

// Server-side-only HTTP client for the Openserve Fulfilment API
// (ITSD-179559 Rev 04.002). Never called directly from a controller —
// OpenserveOrderSubmissionService / the reconciliation worker are the
// only callers, and both persist the raw request/response into
// OpenserveIntegrationLog (with api_key redacted) for support.
//
// BaseUrl is read per-call from IOpenserveRuntimeConfigProvider (DB
// override merged with appsettings/env fallback — see Admin →
// Integrations → Openserve) rather than baked into HttpClient.BaseAddress
// at DI registration time — OpenserveFulfilment is disabled (and BaseUrl
// often empty) by default, and constructing a System.Uri from an empty
// string at startup would throw before the host even finishes composing.
public class OpenserveApiClient : IOpenserveApiClient
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IOpenserveRuntimeConfigProvider _configProvider;
    private readonly ILogger<OpenserveApiClient> _logger;

    public OpenserveApiClient(HttpClient httpClient, IOpenserveRuntimeConfigProvider configProvider, ILogger<OpenserveApiClient> logger)
    {
        _httpClient = httpClient;
        _configProvider = configProvider;
        _logger = logger;
    }

    public async Task<OpenserveApiCallResult<OpenserveCreateOrderOutcome>> CreateOrderAsync(
        OpenserveCreateOrderCommand command, CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        var endpoint = BuildUrl(settings, "productOrder");
        var messageId = Guid.NewGuid().ToString();

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

        return await SendAsync<OpenserveCreateOrderOutcome>(
            HttpMethod.Post, endpoint, messageId, requestJson,
            responseBody =>
            {
                var ack = JsonSerializer.Deserialize<OpenserveAckResponse>(responseBody, ResponseJsonOptions);
                var errorResult = ack?.ErrorResult;
                var isBusinessSuccess = errorResult is not null && errorResult.Code == "0";
                var (parsedOrderId, parsedState) = OpenserveOrderFactsExtractor.TryParseAckMessage(errorResult?.Message);

                // ErrorResult.Code is just the 0/1 success flag (spec
                // examples: "0" on ack, "1" on reject) — the actual
                // Openserve-specific error code (e.g. "GEN-45994",
                // "OS-ESB-ERROR-2000027") lives in Reason. Falls back to
                // Code only if Reason is somehow absent.
                var errorCode = errorResult?.Reason ?? errorResult?.Code;

                return (
                    isBusinessSuccess,
                    errorCode,
                    errorResult?.Message ?? "Openserve returned no ErrorResult.",
                    new OpenserveCreateOrderOutcome(parsedOrderId, parsedState, errorResult?.Message ?? string.Empty));
            },
            cancellationToken);
    }

    public async Task<OpenserveApiCallResult<OpenserveGetOrderOutcome>> GetOrderAsync(
        string openserveOrderId, CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        // Spec §4.6.1 documents the pattern as
        // {BaseUrl}/{ws-ispcode}/productorder/{id} (lowercase
        // "productorder", unlike CREATE's camelCase "productOrder") —
        // followed here even though the worked example in the same
        // section shows a different, unexplained "p2p" path segment
        // and no id at all. Flagged as an open question for Openserve;
        // implemented against the documented pattern, not the
        // inconsistent example.
        var endpoint = BuildUrl(settings, $"productorder/{Uri.EscapeDataString(openserveOrderId)}");
        var messageId = Guid.NewGuid().ToString();

        return await SendAsync<OpenserveGetOrderOutcome>(
            HttpMethod.Get, endpoint, messageId, requestBodyJson: string.Empty,
            responseBody =>
            {
                using var doc = JsonDocument.Parse(responseBody);
                var facts = OpenserveOrderFactsExtractor.Extract(doc.RootElement);
                return (
                    true, null, "OK",
                    new OpenserveGetOrderOutcome(facts.Id, facts.State, facts.OrderName, facts.ServiceOrderNumber, facts.OrderDate));
            },
            cancellationToken);
    }

    public async Task<OpenserveApiCallResult<OpenserveCancelOrderOutcome>> CancelOrderAsync(
        string openserveOrderId, CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        var endpoint = BuildUrl(settings, "cancelproductorder");
        var messageId = Guid.NewGuid().ToString();

        var request = new OpenserveCancelOrderRequest { ProductOrder = new OpenserveOrderRef { Id = openserveOrderId } };
        var requestJson = JsonSerializer.Serialize(request, RequestJsonOptions);

        return await SendAsync<OpenserveCancelOrderOutcome>(
            HttpMethod.Post, endpoint, messageId, requestJson,
            responseBody =>
            {
                var envelope = JsonSerializer.Deserialize<OpenserveResultEnvelope>(responseBody, ResponseJsonOptions);
                var isSuccess = envelope?.Result?.ResultCode == "0";
                return (
                    isSuccess,
                    envelope?.Result?.ResultCode,
                    envelope?.Result?.ResultMsg ?? "Openserve returned no Result.",
                    new OpenserveCancelOrderOutcome(
                        envelope?.Payload?.Id ?? envelope?.Payload?.Order?.Id,
                        envelope?.Payload?.State,
                        envelope?.Payload?.EffectiveCancellationDate));
            },
            cancellationToken);
    }

    public async Task<OpenserveApiCallResult<OpenserveQualificationOutcome>> QualifyAsync(
        OpenserveQualificationQuery query, CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;

        var queryParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Amid))
            queryParams.Add($"AMID={Uri.EscapeDataString(query.Amid)}");
        if (query.Latitude.HasValue)
            queryParams.Add($"LAT={query.Latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (query.Longitude.HasValue)
            queryParams.Add($"LON={query.Longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        queryParams.Add($"BuildingInfo={(query.BuildingInfo ? "Y" : "N")}");

        var endpoint = BuildUrl(settings, "productqualification") + "?" + string.Join("&", queryParams);
        var messageId = Guid.NewGuid().ToString();

        return await SendAsync<OpenserveQualificationOutcome>(
            HttpMethod.Get, endpoint, messageId, requestBodyJson: string.Empty,
            responseBody =>
            {
                var parsed = JsonSerializer.Deserialize<OpenserveQualificationResponse>(responseBody, ResponseJsonOptions);
                var isSuccess = parsed is not null && (parsed.ErrorCode is null or 0);
                var addressInfo = parsed?.Results?.Payload?.AddressInfo;
                var buildings = addressInfo?.buildingNumberInfo?.buildingInfo ?? new List<OpenserveQualificationBuildingInfo>();
                // Only auto-select a buildingNumId when exactly one
                // candidate came back — an MDU with multiple units/
                // buildings at the same AMID cannot be safely
                // disambiguated without the customer's unit number, and
                // buildingNumId is optional per Appendix C, so leaving
                // it null here is correct, not a defect.
                var buildingNumId = buildings.Count == 1 ? buildings[0].BLD_NUM_ID : null;
                var ftthInfo = parsed?.Results?.Payload?.FtthInfrastructure?.ftthInfo?.FirstOrDefault();

                var products = ftthInfo?.ftthProductInfo?
                    .Select(p => new OpenserveQualificationProduct(p.ProductName, p.ProductCode, p.upstreamSpeed, p.downstreamSpeed))
                    .ToList();

                var outcome = new OpenserveQualificationOutcome(
                    addressInfo?.AMID,
                    buildingNumId,
                    buildings.Count,
                    addressInfo?.LR_Address,
                    ftthInfo?.FTTH_Status,
                    ftthInfo?.fibreMaxSpeed,
                    ftthInfo?.fibreMaxSpeedUnit,
                    addressInfo?.LR_SUBURB,
                    addressInfo?.LR_TOWN,
                    addressInfo?.LR_PROVINCE,
                    products);

                return (
                    isSuccess,
                    parsed?.ErrorCode?.ToString(),
                    parsed?.Message is { Length: > 0 } m ? m : parsed?.ErrorString ?? "Openserve returned no payload.",
                    outcome);
            },
            cancellationToken);
    }

    public async Task<OpenserveApiCallResult<OpenserveGetActionsOutcome>> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var settings = _configProvider.Current;
        // Spec §4.8.1 example: /upp/getactions/?IspCode=ISPCODE&actionName=Order
        // OSS Delay&ponr=No — deliberately NOT under {ws-ispcode}/ like every
        // other endpoint; IspCode here is the "WS <NAME>"-pattern ISP
        // Identifier value per the spec's own sample ("WS AWESOMEISP"), not
        // the {ws-ispcode} URL segment used elsewhere.
        var ispCode = Uri.EscapeDataString(settings.IspIdentifier ?? string.Empty);
        var endpoint = $"{settings.BaseUrl.TrimEnd('/')}/upp/getactions/?IspCode={ispCode}&actionName=Order+OSS+Delay&ponr=No";
        var messageId = Guid.NewGuid().ToString();

        return await SendAsync<OpenserveGetActionsOutcome>(
            HttpMethod.Get, endpoint, messageId, requestBodyJson: string.Empty,
            responseBody =>
            {
                var parsed = JsonSerializer.Deserialize<OpenserveGetActionsResponse>(responseBody, ResponseJsonOptions);
                var resultCode = ParseResultCode(parsed?.Result?.ResultCode);
                var isSuccess = resultCode is null or 0;
                var objectCount = parsed?.Payload?.TotalObjects?.Count ?? 0;

                return (
                    isSuccess,
                    parsed?.Result?.ResultCode,
                    parsed?.Result?.ResultMsg ?? "Openserve returned no Result.",
                    new OpenserveGetActionsOutcome(resultCode, parsed?.Result?.ResultMsg, objectCount));
            },
            cancellationToken);
    }

    private static int? ParseResultCode(string? raw)
        => int.TryParse(raw, out var code) ? code : null;

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

    private async Task<OpenserveApiCallResult<TOutcome>> SendAsync<TOutcome>(
        HttpMethod method, string endpoint, string messageId, string requestBodyJson,
        Func<string, (bool IsSuccess, string? ErrorCode, string? ErrorMessage, TOutcome Outcome)> parseResponse,
        CancellationToken cancellationToken)
    {
        var settings = _configProvider.Current;

        using var httpRequest = new HttpRequestMessage(method, endpoint);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Headers.TryAddWithoutValidation("api_key", settings.ApiKey);
        httpRequest.Headers.TryAddWithoutValidation("MessageID", messageId);
        if (!string.IsNullOrWhiteSpace(settings.ReplyToAddress))
            httpRequest.Headers.TryAddWithoutValidation("ReplyToAddress", settings.ReplyToAddress);
        if (!string.IsNullOrWhiteSpace(settings.SenderId))
            httpRequest.Headers.TryAddWithoutValidation("SenderID", settings.SenderId);

        if (method != HttpMethod.Get)
        {
            httpRequest.Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Openserve {Method} {Endpoint} returned HTTP {Status}. messageId={MessageId}",
                    method, endpoint, (int)response.StatusCode, messageId);
                return OpenserveApiCallResult<TOutcome>.Failure(
                    messageId, method.Method, endpoint, (int)response.StatusCode,
                    requestBodyJson, responseBody, errorCode: response.StatusCode.ToString(),
                    errorMessage: $"Openserve returned HTTP {(int)response.StatusCode}.");
            }

            (bool isSuccess, string? errorCode, string? errorMessage, TOutcome outcome) parsed;
            try
            {
                parsed = parseResponse(responseBody);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Openserve {Method} {Endpoint} returned unparseable JSON. messageId={MessageId}",
                    method, endpoint, messageId);
                return OpenserveApiCallResult<TOutcome>.Failure(
                    messageId, method.Method, endpoint, (int)response.StatusCode,
                    requestBodyJson, responseBody, errorCode: "PARSE_ERROR",
                    errorMessage: "Openserve response could not be parsed.");
            }

            if (!parsed.isSuccess)
            {
                return OpenserveApiCallResult<TOutcome>.Failure(
                    messageId, method.Method, endpoint, (int)response.StatusCode,
                    requestBodyJson, responseBody, parsed.errorCode, parsed.errorMessage ?? "Openserve rejected the request.");
            }

            return OpenserveApiCallResult<TOutcome>.Success(
                messageId, method.Method, endpoint, (int)response.StatusCode,
                requestBodyJson, responseBody, parsed.outcome);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Openserve {Method} {Endpoint} timed out. messageId={MessageId}", method, endpoint, messageId);
            return OpenserveApiCallResult<TOutcome>.Failure(
                messageId, method.Method, endpoint, null, requestBodyJson, null,
                errorCode: "TIMEOUT", errorMessage: "Openserve request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Openserve {Method} {Endpoint} transport failure. messageId={MessageId}", method, endpoint, messageId);
            return OpenserveApiCallResult<TOutcome>.Failure(
                messageId, method.Method, endpoint, null, requestBodyJson, null,
                errorCode: "TRANSPORT_ERROR", errorMessage: ex.Message);
        }
    }

    private static string BuildUrl(OpenserveFulfilmentSettings settings, string path)
        => $"{settings.BaseUrl.TrimEnd('/')}/{settings.WsIspCode.Trim('/')}/{path}";
}

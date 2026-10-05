namespace SmartFuture.Application.Openserve;

/// <summary>
/// Server-side-only client for the Openserve Fulfilment API. The
/// executable contract is Openserve's provisioned Postman collection
/// "Fulfilment API Collection (Broadband)" v02 — where it disagrees with
/// the PDF (ITSD-179559 Rev 04.002) the collection wins, because it is
/// what Openserve configured for Smart Future's staging tenant. Every
/// method builds a fresh MessageID per call (spec: "Must be unique UUID
/// per invocation") and returns a result carrying enough sanitized
/// request/response detail for OpenserveIntegrationLog, without ever
/// surfacing the api_key.
///
/// Header profile per operation (taken verbatim from the collection —
/// FromLocation is deliberately NOT one global value):
///   Product Qualification: MessageID, FromLocation={isp_tag}, SenderID, api_key
///   Product Ordering (productorder / getproductorder / cancelproductorder):
///     MessageID, FromLocation={ISPID}, SenderID, ReplyToAddress, api_key
/// </summary>
public interface IOpenserveApiClient
{
    /// <summary>POST https://{HOST_URL}/{isp_tag}/productorder — Postman "UC 1: Create New Order".</summary>
    Task<OpenserveApiCallResult<OpenserveCreateOrderOutcome>> CreateOrderAsync(OpenserveCreateOrderCommand command, CancellationToken cancellationToken = default);

    /// <summary>GET https://{HOST_URL}/{isp_tag}/getproductorder/{order_id} — Postman "Query Order Details". Read-only.</summary>
    Task<OpenserveApiCallResult<OpenserveGetOrderOutcome>> GetOrderAsync(string openserveOrderId, CancellationToken cancellationToken = default);

    /// <summary>POST https://{HOST_URL}/{isp_tag}/cancelproductorder — Postman "Cancel Inflight Order". Only ever reached from an explicit admin Cancel on a known order.</summary>
    Task<OpenserveApiCallResult<OpenserveCancelOrderOutcome>> CancelOrderAsync(string openserveOrderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET https://{HOST_URL}/{isp_tag}/productqualification?AMID=..&amp;BuildingInfo=Y, or
    /// ?LAT=..&amp;LON=..&amp;BuildingInfo=Y — Postman "productQualification AMID" /
    /// "productQualification LatLong". Read-only. When an AMID is supplied it is
    /// sent on its own (the collection never combines AMID with LAT/LON).
    /// </summary>
    Task<OpenserveApiCallResult<OpenserveQualificationOutcome>> QualifyAsync(OpenserveQualificationQuery query, CancellationToken cancellationToken = default);
}

public class OpenserveQualificationQuery
{
    public string? Amid { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    /// <summary>Spec: "Optional (N/Y): if address is MDU, a list of building info will be returned."</summary>
    public bool BuildingInfo { get; init; } = true;
}

/// <summary>Facts extracted from a Product Qualification response. <see cref="Buildings"/> carries every MDU building/unit row verbatim so the order flow can match the customer's unit and send buildingName/floor/unit/buildingNumId exactly as qualification returned them (Postman UC 1 place remarks). <see cref="Facts"/> is the complete parse — every FTTH entry and product, the canonical address and DIST_M — which is what Fibre eligibility is decided on (an AMID alone proves nothing about Fibre). FtthStatus/FibreMaxSpeed are the first immediately-available FTTH entry's (else the first entry's); AvailableProducts lists the products of every entry.</summary>
public sealed record OpenserveQualificationOutcome(
    string? Amid, string? BuildingNumId, int BuildingMatchCount, string? MatchedAddress, string? FtthStatus,
    decimal? FibreMaxSpeed, string? FibreMaxSpeedUnit, string? Suburb = null, string? Town = null, string? Province = null,
    IReadOnlyList<OpenserveQualificationProduct>? AvailableProducts = null, IReadOnlyList<OpenserveQualificationBuilding>? Buildings = null,
    OpenserveQualificationFacts? Facts = null);

public sealed record OpenserveQualificationProduct(string? ProductName, string? ProductCode, string? UpstreamSpeed, string? DownstreamSpeed);

/// <summary>One buildingInfo row from Product Qualification (§3.1.1.3) — values kept exactly as Openserve returned them.</summary>
public sealed record OpenserveQualificationBuilding(string? AmId, string? BldNumId, string? BldId, string? FloorId, string? Num, string? BuildingName, string? Floor);

/// <summary>Everything needed to build a CREATE Product Order (Sales Order / new provide) request. Deliberately flat — the caller (OpenserveOrderSubmissionService) already resolved package mapping, AMID, subscriber reference, etc.</summary>
public class OpenserveCreateOrderCommand
{
    public required string ExternalReferenceNumber { get; init; }
    public required string OpenserveProductName { get; init; }
    public required string Sku { get; init; }
    public required string Capacity { get; init; }
    public required string CapacityUom { get; init; }

    public string? SubscriberReferenceNumber { get; init; }
    public string? SubscriberContactName { get; init; }
    public string? SubscriberContactPhone { get; init; }
    public required string IspIdentifier { get; init; }

    public string? BuildingName { get; init; }
    public string? Unit { get; init; }
    public string? Floor { get; init; }
    public required string Street1 { get; init; }
    public string? Suburb { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? Longitude { get; init; }
    public string? Latitude { get; init; }
    public string? BuildingNumId { get; init; }
    public required string Amid { get; init; }

    public string? Comment { get; init; }
    public DateTime? RequestedStartDateUtc { get; init; }
}

/// <summary>Generic envelope for a single Openserve HTTP call — carries what OpenserveIntegrationLog needs regardless of success/failure.</summary>
public class OpenserveApiCallResult<TOutcome>
{
    public required bool IsSuccess { get; init; }
    public required string MessageId { get; init; }
    public required string HttpMethod { get; init; }
    public required string Endpoint { get; init; }
    public int? HttpStatusCode { get; init; }
    public required string RequestBodyJson { get; init; }

    /// <summary>JSON object of the headers actually sent, with api_key replaced by <see cref="OpenserveHeaderRedaction.RedactedValue"/>. Safe to persist and to return to the admin console — the real key never enters this string.</summary>
    public string? RequestHeadersJson { get; init; }

    public string? ResponseBodyJson { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public TOutcome? Outcome { get; init; }

    /// <summary>True when Openserve answered with HTTP 2xx — i.e. the request was routed, authenticated and processed, even if the business result was a rejection.</summary>
    public bool ReachedOpenserve => HttpStatusCode is >= 200 and < 300;

    public static OpenserveApiCallResult<TOutcome> Success(string messageId, string method, string endpoint, int httpStatusCode, string requestBodyJson, string responseBodyJson,
        TOutcome outcome, string? requestHeadersJson = null) => new()
    {
        IsSuccess = true,
        MessageId = messageId,
        HttpMethod = method,
        Endpoint = endpoint,
        HttpStatusCode = httpStatusCode,
        RequestBodyJson = requestBodyJson,
        RequestHeadersJson = requestHeadersJson,
        ResponseBodyJson = responseBodyJson,
        Outcome = outcome
    };

    public static OpenserveApiCallResult<TOutcome> Failure(string messageId, string method, string endpoint, int? httpStatusCode, string requestBodyJson, string? responseBodyJson,
        string? errorCode, string errorMessage, string? requestHeadersJson = null) => new()
    {
        IsSuccess = false,
        MessageId = messageId,
        HttpMethod = method,
        Endpoint = endpoint,
        HttpStatusCode = httpStatusCode,
        RequestBodyJson = requestBodyJson,
        RequestHeadersJson = requestHeadersJson,
        ResponseBodyJson = responseBodyJson,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

/// <summary>Single place that decides what a persisted/displayed copy of an outbound Openserve header set looks like.</summary>
public static class OpenserveHeaderRedaction
{
    public const string RedactedValue = "***REDACTED***";

    /// <summary>Header names whose values must never be persisted, logged or returned to any client.</summary>
    public static readonly IReadOnlySet<string> SecretHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api_key", "Authorization" };

    public static string ToSanitizedJson(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var sanitized = new Dictionary<string, string>();
        foreach (var (name, value) in headers)
        {
            sanitized[name] = SecretHeaderNames.Contains(name) ? RedactedValue : value;
        }
        return System.Text.Json.JsonSerializer.Serialize(sanitized);
    }
}

/// <summary>Best-effort facts parsed from the sync ack ("Order received for processing. Order Id = 1742148. State = Validated"). The order id parsed here is the correlation key for reconciliation polling and any later callback/event.</summary>
public sealed record OpenserveCreateOrderOutcome(string? ParsedOrderId, string? ParsedState, string AckMessage);

/// <summary>Facts from Query Order Details. CircuitNumber is the realizingService name (B-number/DN) once Openserve has allocated one.</summary>
public sealed record OpenserveGetOrderOutcome(string? Id, string? State, string? OrderName, string? ServiceOrderNumber, string? OrderDate, string? OrderType = null, string? CircuitNumber = null);

public sealed record OpenserveCancelOrderOutcome(string? Id, string? State, string? EffectiveCancellationDate);

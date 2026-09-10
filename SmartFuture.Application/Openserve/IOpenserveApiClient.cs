namespace SmartFuture.Application.Openserve;

/// <summary>
/// Server-side-only client for the Openserve Fulfilment API
/// (ITSD-179559 Rev 04.002). Every method builds a fresh MessageID
/// per call (spec: "Must be unique UUID per invocation") and returns a
/// result carrying enough raw request/response detail for
/// OpenserveIntegrationLog, without ever surfacing the api_key.
/// </summary>
public interface IOpenserveApiClient
{
    /// <summary>POST {BaseUrl}/{ws-ispcode}/productOrder — spec §4.1.</summary>
    Task<OpenserveApiCallResult<OpenserveCreateOrderOutcome>> CreateOrderAsync(
        OpenserveCreateOrderCommand command, CancellationToken cancellationToken = default);

    /// <summary>GET {BaseUrl}/{ws-ispcode}/productorder/{id} — spec §4.6.1.</summary>
    Task<OpenserveApiCallResult<OpenserveGetOrderOutcome>> GetOrderAsync(
        string openserveOrderId, CancellationToken cancellationToken = default);

    /// <summary>POST {BaseUrl}/{ws-ispcode}/cancelproductorder — spec §4.7.</summary>
    Task<OpenserveApiCallResult<OpenserveCancelOrderOutcome>> CancelOrderAsync(
        string openserveOrderId, CancellationToken cancellationToken = default);

    /// <summary>GET {BaseUrl}/{ws-ispcode}/productqualification?... — spec §3. Pass either amid, or latitude+longitude (not both required — spec's own two worked examples use one or the other).</summary>
    Task<OpenserveApiCallResult<OpenserveQualificationOutcome>> QualifyAsync(
        OpenserveQualificationQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET {BaseUrl}/upp/getactions/?IspCode=...&amp;actionName=Order OSS Delay&amp;ponr=No —
    /// spec §4.8.1. Used ONLY as the admin "Test Connection" probe: it's
    /// the one documented GET that needs no coordinates/AMID and no
    /// existing order id, and is explicitly read-only ("retrieve orders
    /// that have a Delay action"). A well-formed response — even an
    /// empty result set — proves DNS/TLS/api_key/IspIdentifier are all
    /// accepted; an auth/ISP-code error proves exactly what's wrong.
    /// Never creates, updates, or cancels anything.
    /// </summary>
    Task<OpenserveApiCallResult<OpenserveGetActionsOutcome>> TestConnectionAsync(CancellationToken cancellationToken = default);
}

public sealed record OpenserveGetActionsOutcome(int? ResultCode, string? ResultMsg, int ObjectCount);

public class OpenserveQualificationQuery
{
    public string? Amid { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    /// <summary>Spec: "Optional (N/Y): if address is MDU, a list of building info will be returned."</summary>
    public bool BuildingInfo { get; init; } = true;
}

/// <summary>Facts extracted from a Product Qualification response. Suburb/Town/Province/AvailableProducts are only populated for the admin diagnostic tool (Admin → Integrations → Openserve → API Tests) — QualifyOrderAsync's order-flow use only needs Amid/BuildingNumId.</summary>
public sealed record OpenserveQualificationOutcome(
    string? Amid,
    string? BuildingNumId,
    int BuildingMatchCount,
    string? MatchedAddress,
    string? FtthStatus,
    decimal? FibreMaxSpeed,
    string? FibreMaxSpeedUnit,
    string? Suburb = null,
    string? Town = null,
    string? Province = null,
    IReadOnlyList<OpenserveQualificationProduct>? AvailableProducts = null);

public sealed record OpenserveQualificationProduct(string? ProductName, string? ProductCode, string? UpstreamSpeed, string? DownstreamSpeed);

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
    public string? ResponseBodyJson { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public TOutcome? Outcome { get; init; }

    public static OpenserveApiCallResult<TOutcome> Success(
        string messageId, string method, string endpoint, int httpStatusCode,
        string requestBodyJson, string responseBodyJson, TOutcome outcome) => new()
    {
        IsSuccess = true,
        MessageId = messageId,
        HttpMethod = method,
        Endpoint = endpoint,
        HttpStatusCode = httpStatusCode,
        RequestBodyJson = requestBodyJson,
        ResponseBodyJson = responseBodyJson,
        Outcome = outcome
    };

    public static OpenserveApiCallResult<TOutcome> Failure(
        string messageId, string method, string endpoint, int? httpStatusCode,
        string requestBodyJson, string? responseBodyJson, string? errorCode, string errorMessage) => new()
    {
        IsSuccess = false,
        MessageId = messageId,
        HttpMethod = method,
        Endpoint = endpoint,
        HttpStatusCode = httpStatusCode,
        RequestBodyJson = requestBodyJson,
        ResponseBodyJson = responseBodyJson,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

/// <summary>Best-effort facts parsed from the sync ack. The Openserve numeric order id is NOT reliably known until the async callback arrives — see OpenserveOrderSubmissionService remarks.</summary>
public sealed record OpenserveCreateOrderOutcome(string? ParsedOrderId, string? ParsedState, string AckMessage);

public sealed record OpenserveGetOrderOutcome(string? Id, string? State, string? OrderName, string? ServiceOrderNumber, string? OrderDate);

public sealed record OpenserveCancelOrderOutcome(string? Id, string? State, string? EffectiveCancellationDate);

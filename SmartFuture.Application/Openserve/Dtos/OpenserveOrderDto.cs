using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve.Dtos;

public class OpenserveOrderDto
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? PackageName { get; set; }

    public string ExternalReferenceNumber { get; set; } = string.Empty;
    public string? SubscriberReferenceNumber { get; set; }
    public string? OpenserveOrderId { get; set; }
    public string? OpenserveOrderName { get; set; }

    public string OrderType { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? RawState { get; set; }
    /// <summary>Serialized as a name (e.g. "InProgress"), not a number — no JsonStringEnumConverter is registered on the API, so a raw enum property here would ship over JSON as a bare integer.</summary>
    public string NormalizedStatus { get; set; } = string.Empty;
    public bool IsTerminal { get; set; }

    public string? Sku { get; set; }
    public string? Capacity { get; set; }
    public string? CapacityUom { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? LastOpenserveUpdateAtUtc { get; set; }
    public DateTime? LastSuccessfulSyncAtUtc { get; set; }

    public int RetryCount { get; set; }
    public string? LastFailureCode { get; set; }
    public string? LastFailureMessage { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class OpenserveOrderStatusHistoryDto
{
    public Guid Id { get; set; }
    public Guid OpenserveOrderId { get; set; }
    public Guid OrderId { get; set; }
    public string? OpenserveEventId { get; set; }
    public string? CorrelationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? PreviousRawState { get; set; }
    public string? NewRawState { get; set; }
    public string NormalizedStatus { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? InstallationStatus { get; set; }
    public DateTime? AppointmentAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? EventOccurredAtUtc { get; set; }
    public Guid? IntegrationLogId { get; set; }
    public string? ProcessingResult { get; set; }
    public bool NotificationTriggered { get; set; }
}

public class OpenserveIntegrationLogDto
{
    public Guid Id { get; set; }
    public Guid? OpenserveOrderId { get; set; }
    public string? OpenserveOrderExternalReferenceNumber { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }
    public string? HttpMethod { get; set; }
    public string? Endpoint { get; set; }
    /// <summary>Redacted before persisting (see OpenserveIntegrationLog remarks) — safe to display verbatim.</summary>
    public string? RequestHeadersJson { get; set; }
    public string? RequestBodyJson { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBodyJson { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorSummary { get; set; }
}

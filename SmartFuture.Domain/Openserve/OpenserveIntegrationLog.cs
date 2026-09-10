using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Domain.Openserve;

// Raw request/response capture for every Openserve HTTP interaction
// (outbound calls we make, and inbound callbacks/events Openserve
// sends us) — the "View request/response log" admin feature reads
// this. api_key and any callback shared-secret MUST be redacted
// before RequestHeadersJson is written; never persist secrets here.
public class OpenserveIntegrationLog : BaseEntity
{
    public Guid? OpenserveOrderId { get; set; }
    public OpenserveOrder? OpenserveOrder { get; set; }

    public OpenserveIntegrationDirection Direction { get; set; }
    public OpenserveOperationType OperationType { get; set; }

    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }

    public string? HttpMethod { get; set; }
    public string? Endpoint { get; set; }

    /// <summary>Redacted (api_key / signature / secret headers stripped) before persisting.</summary>
    public string? RequestHeadersJson { get; set; }
    public string? RequestBodyJson { get; set; }

    public int? ResponseStatusCode { get; set; }
    public string? ResponseBodyJson { get; set; }

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsSuccess { get; set; }
    public string? ErrorSummary { get; set; }
}

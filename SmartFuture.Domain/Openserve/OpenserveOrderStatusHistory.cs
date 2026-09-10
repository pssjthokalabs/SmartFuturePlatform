using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Domain.Openserve;

// Append-only audit trail for every meaningful Openserve update
// (webhook event OR reconciliation poll — both flow through the same
// pipeline and land here identically, per brief §15). Never deleted or
// overwritten by a later event.
public class OpenserveOrderStatusHistory : BaseEntity
{
    public Guid OpenserveOrderId { get; set; }
    public OpenserveOrder? OpenserveOrder { get; set; }

    /// <summary>Denormalized so admin can query history by SmartFuture OrderId without a join.</summary>
    public Guid OrderId { get; set; }

    public string? OpenserveEventId { get; set; }
    public string? CorrelationId { get; set; }
    public OpenserveEventType EventType { get; set; } = OpenserveEventType.Unknown;

    public string? PreviousRawState { get; set; }
    public string? NewRawState { get; set; }
    public OpenserveProvisioningStatus NormalizedStatus { get; set; }

    /// <summary>Openserve's free-text "description" field — human-readable supplement only, never the source of truth for state (spec's Cancel* examples show description and state can disagree).</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Free text. The confirmed spec has NO dedicated installation/
    /// appointment fields (see project notes) — this column exists so
    /// the schema doesn't need a migration the day Openserve confirms
    /// one, but nothing populates it yet.
    /// </summary>
    public string? InstallationStatus { get; set; }

    /// <summary>Structurally ready, unpopulated until an appointment field is confirmed in the spec.</summary>
    public DateTime? AppointmentAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EventOccurredAtUtc { get; set; }

    /// <summary>Pointer to the raw payload capture, avoids duplicating the full JSON in every history row.</summary>
    public Guid? IntegrationLogId { get; set; }
    public OpenserveIntegrationLog? IntegrationLog { get; set; }

    /// <summary>e.g. "Applied", "IgnoredDuplicate", "IgnoredOutOfOrder", "IgnoredUnknownOrder", "Error".</summary>
    public string? ProcessingResult { get; set; }

    public bool NotificationTriggered { get; set; }
}

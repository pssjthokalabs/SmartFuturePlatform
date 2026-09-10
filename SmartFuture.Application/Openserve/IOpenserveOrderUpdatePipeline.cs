using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Everything the pipeline needs to process ONE inbound Openserve
/// update, regardless of source (callback, event notification, or
/// reconciliation GET). The caller has already logged the raw payload
/// to OpenserveIntegrationLog and passes its id through
/// <see cref="IntegrationLogId"/> so history rows can point back to it.
/// </summary>
public class OpenserveUpdateInput
{
    /// <summary>Openserve's own numeric order id — the primary correlation key (see OpenserveOrderUpdatePipeline remarks on why ExternalReferenceNumber can't be used here).</summary>
    public string? OpenserveOrderId { get; init; }

    public required string RawState { get; init; }
    public string? Description { get; init; }
    public OpenserveEventType EventType { get; init; } = OpenserveEventType.Unknown;
    public string? OpenserveEventId { get; init; }
    public string? CorrelationId { get; init; }
    public string? OrderName { get; init; }
    public DateTime? EventOccurredAtUtc { get; init; }
    public Guid? IntegrationLogId { get; init; }

    /// <summary>True when this update came from the reconciliation worker's GET rather than a pushed callback/event — affects duplicate/no-change handling (no eventId to dedupe on).</summary>
    public bool IsReconciliation { get; init; }
}

public enum OpenserveUpdateResultKind
{
    /// <summary>A genuinely new state was recorded and OpenserveOrder's current fields were updated.</summary>
    Applied,

    /// <summary>Same OpenserveEventId already processed — no new history, no notification.</summary>
    Duplicate,

    /// <summary>Reconciliation found the same RawState as already on file — nothing to record beyond bumping LastSuccessfulSyncAtUtc.</summary>
    NoChange,

    /// <summary>Recorded in history for audit completeness, but arrived older than the last known update — OpenserveOrder's current fields were NOT regressed.</summary>
    AppliedOutOfOrder,

    /// <summary>No OpenserveOrder row could be matched to this update (brief §7: "temporarily missing SmartFuture correlation" must be tolerated, not thrown).</summary>
    UnknownOrder,

    Error
}

public class OpenserveUpdateOutcome
{
    public required OpenserveUpdateResultKind Kind { get; init; }
    public Guid? OpenserveOrderId { get; init; }
    public string? Message { get; init; }
}

public interface IOpenserveOrderUpdatePipeline
{
    /// <summary>
    /// The SINGLE handler every Openserve update source funnels
    /// through (brief Priority 3). Never throws — callers (controllers,
    /// the reconciliation worker) get a typed outcome back instead.
    /// </summary>
    Task<OpenserveUpdateOutcome> ApplyUpdateAsync(OpenserveUpdateInput input, CancellationToken cancellationToken = default);
}

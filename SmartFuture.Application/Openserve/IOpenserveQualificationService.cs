using SmartFuture.Domain.Orders;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Runs the Openserve Product Qualification API (spec §3) against an
/// order's installation address and writes the result directly onto
/// the Order — this is the ONLY writer of Order.OpenserveAmId/
/// OpenserveBuildingNumId/OpenserveQualifiedAtUtc/
/// OpenserveQualificationFailureReason. Never throws; a failed lookup
/// just leaves OpenserveAmId null with a reason recorded, and order
/// creation proceeds regardless — OpenserveOrderSubmissionService
/// already blocks (visibly) on a missing AMID later.
/// </summary>
public interface IOpenserveQualificationService
{
    /// <summary>
    /// Mutates the given (already in-memory, not-yet-necessarily-saved)
    /// Order entity in place. Caller is responsible for persisting the
    /// Order itself (this method independently persists its own
    /// OpenserveIntegrationLog row for traceability regardless of
    /// whether the Order save happens before or after). Used while an
    /// order is being created (CreateMineAsync / CreateFreeActivationMineAsync).
    /// </summary>
    Task QualifyOrderAsync(Order order, CancellationToken cancellationToken = default);

    /// <summary>
    /// The shared qualification routine for an order that already exists —
    /// payment-first conversion, the submission coordinator's self-heal, and
    /// Admin "Run Product Qualification" all call this. Loads the order, runs
    /// <see cref="QualifyOrderAsync"/>, persists the result and audits it.
    /// Never re-qualifies an order that already has an AMID, never calls
    /// Openserve without usable coordinates, and (unless
    /// <paramref name="ignoreCooldown"/>) does not repeat a recent failed
    /// attempt. Never throws.
    /// </summary>
    Task<OpenserveQualificationRunResult> QualifyAndPersistAsync(Guid orderId, OpenserveQualificationTrigger trigger, bool ignoreCooldown = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads the building/unit rows for the order's EXISTING AMID (query by
    /// AMID, BuildingInfo=Y) and stores them — for orders qualified before the
    /// candidates were recorded. Never changes the AMID; picks a row only when
    /// that is deterministic. Never throws.
    /// </summary>
    Task<OpenserveQualificationRunResult> RefreshBuildingCandidatesAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>True when the order (or its linked CoverageRequest) has real coordinates to qualify with.</summary>
    Task<bool> HasUsableCoordinatesAsync(Order order, CancellationToken cancellationToken = default);
}

/// <summary>Which path asked for qualification — recorded in the audit trail.</summary>
public enum OpenserveQualificationTrigger
{
    OrderCreated = 0,
    PaymentConversion = 1,
    SubmissionSelfHeal = 2,
    AdminManual = 3,
    BuildingCandidatesRefresh = 4
}

public enum OpenserveQualificationRunStatus
{
    /// <summary>Openserve returned an AMID; it is stored on the order.</summary>
    Qualified = 0,

    /// <summary>Openserve was called but no AMID came back (HTTP/business failure, or no AMID for the address).</summary>
    NoAmid = 1,

    /// <summary>No usable coordinates — Openserve was NOT called.</summary>
    NoCoordinates = 2,

    /// <summary>Nothing done (not Fibre, AMID already present, integration disabled, recent failure).</summary>
    Skipped = 3,

    NotFound = 4
}

public sealed record OpenserveQualificationRunResult(OpenserveQualificationRunStatus Status, string Message, string? AmId = null, string? BuildingNumId = null)
{
    /// <summary>A Product Qualification request actually went to Openserve on this run.</summary>
    public bool CalledOpenserve => Status is OpenserveQualificationRunStatus.Qualified or OpenserveQualificationRunStatus.NoAmid;
}

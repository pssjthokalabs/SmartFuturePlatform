using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Runs the Openserve Product Qualification API (spec §3) and records the
/// result as evidence (OpenserveQualificationResults): address identified
/// (AMID + canonical address), Fibre availability, the products/speeds
/// offered, whether the address is the customer's and whether the package's
/// mapped product is eligible. The ONLY writer of Order.OpenserveAmId /
/// OpenserveBuildingNumId / OpenserveQualifiedAtUtc /
/// OpenserveQualificationFailureReason / OpenserveQualificationResultId.
///
/// While the integration can qualify (<see cref="CanQualify"/>), this is
/// also the Fibre eligibility authority for customers: the coverage check
/// lists only packages it finds eligible, and checkout refuses any other.
/// Never throws.
/// </summary>
public interface IOpenserveQualificationService
{
    /// <summary>The integration is enabled and configured for Product Qualification — it, not the public GIS lookup, decides Fibre eligibility.</summary>
    bool CanQualify { get; }

    /// <summary>
    /// Mutates the given (already in-memory, not-yet-necessarily-saved)
    /// Order entity in place. Caller is responsible for persisting the
    /// Order itself (this method independently persists its own
    /// OpenserveIntegrationLog + evidence rows regardless of whether the
    /// Order save happens before or after). Used while an order is being
    /// created (CreateMineAsync / CreateFreeActivationMineAsync).
    /// </summary>
    Task QualifyOrderAsync(Order order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="QualifyOrderAsync(Order, CancellationToken)"/>, but first tries the evidence the
    /// checkout gate just recorded for this order's address — no second
    /// Openserve call when it is valid for the order's coordinates.
    /// </summary>
    Task QualifyOrderFromCheckoutAsync(Order order, Guid? checkoutEvidenceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The shared qualification routine for an order that already exists —
    /// payment-first conversion, the submission coordinator's self-heal, and
    /// Admin "Run Product Qualification" all call this. Loads the order, runs
    /// the qualification, persists the result + evidence and audits it.
    /// Automatic triggers never re-qualify an order that already has an AMID
    /// AND current evidence; Admin may re-run (the order must not be with
    /// Openserve — the caller checks). Never calls Openserve without usable
    /// coordinates and (unless <paramref name="ignoreCooldown"/>) does not
    /// repeat a recent attempt. Never throws.
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

    /// <summary>
    /// Customer coverage check: authenticated Product Qualification for a
    /// location (a very recent result for the same coordinates is reused),
    /// evaluated against every active Fibre package's enabled mapping.
    /// </summary>
    Task<OpenserveLocationEligibility> EvaluateLocationAsync(OpenserveLocationQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Backend checkout gate for one Fibre package — run before payment or
    /// order creation, so a bypassed frontend can't buy an ineligible
    /// package. Records the evidence it relied on (EvidenceId).
    /// </summary>
    Task<OpenserveCheckoutGateResult> CheckFibreCheckoutAsync(OpenserveLocationQuery query, Guid servicePackageId, CancellationToken cancellationToken = default);
}

/// <summary>The customer's location as entered at checkout / coverage check.</summary>
public sealed record OpenserveLocationQuery(decimal? Latitude, decimal? Longitude, string? AddressLine1, string? Suburb, string? City, string? Province)
{
    public OpenserveAddressMatcher.CustomerAddress Customer => new(AddressLine1, Suburb, City, Province);
}

public enum OpenserveLocationStatus
{
    /// <summary>The integration can't qualify (disabled / not configured) — the caller keeps its legacy behaviour.</summary>
    NotAuthoritative = 0,

    /// <summary>No usable coordinates — Openserve was not called.</summary>
    NoCoordinates = 1,

    /// <summary>Openserve couldn't be asked or didn't answer usefully (HTTP/transport/business error).</summary>
    QualificationUnavailable = 2,

    /// <summary>Evidence evaluated — see Location and Packages.</summary>
    Evaluated = 3
}

public sealed class OpenserveLocationEligibility
{
    public OpenserveLocationStatus Status { get; init; }
    public Guid? EvidenceId { get; init; }

    /// <summary>Server-side only — never serialised to a customer.</summary>
    public OpenserveQualificationResult? Evidence { get; init; }

    /// <summary>The location-level verdict (address + Fibre), without a package.</summary>
    public OpenserveEligibilityAssessment? Location { get; init; }
    public IReadOnlyList<OpenservePackageEligibility> Packages { get; init; } = Array.Empty<OpenservePackageEligibility>();
    public string CustomerTitle { get; init; } = string.Empty;
    public string CustomerMessage { get; init; } = string.Empty;

    public static OpenserveLocationEligibility NotAuthoritative { get; } = new() { Status = OpenserveLocationStatus.NotAuthoritative };
}

public sealed record OpenservePackageEligibility(Guid ServicePackageId, OpenserveEligibilityAssessment Assessment);

/// <summary>Checkout gate verdict. <see cref="Applies"/> false = the integration can't qualify, so the gate doesn't apply (legacy behaviour).</summary>
public sealed record OpenserveCheckoutGateResult(bool Applies, bool Allowed, string? ErrorCode = null, string? Message = null, Guid? EvidenceId = null)
{
    public static OpenserveCheckoutGateResult NotApplicable { get; } = new(false, true);
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
    /// <summary>Openserve returned an AMID (address identified); it is stored on the order. Fibre/product eligibility is reported separately (FibreEligible) — an AMID is not Fibre coverage.</summary>
    Qualified = 0,

    /// <summary>Openserve was called but no AMID came back (HTTP/business failure, or no AMID for the address).</summary>
    NoAmid = 1,

    /// <summary>No usable coordinates — Openserve was NOT called.</summary>
    NoCoordinates = 2,

    /// <summary>Nothing done (not Fibre, AMID already present, integration disabled, recent failure).</summary>
    Skipped = 3,

    NotFound = 4
}

/// <param name="FibreEligible">For a run that identified the address: whether the order's package is orderable there (Fibre available, product/capacity offered, address confirmed). Null when not evaluated.</param>
public sealed record OpenserveQualificationRunResult(OpenserveQualificationRunStatus Status, string Message, string? AmId = null, string? BuildingNumId = null, bool? FibreEligible = null)
{
    /// <summary>A Product Qualification request actually went to Openserve on this run.</summary>
    public bool CalledOpenserve => Status is OpenserveQualificationRunStatus.Qualified or OpenserveQualificationRunStatus.NoAmid;
}

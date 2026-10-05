using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Domain.Openserve;

// One Openserve Product Qualification run (spec §3) and what SmartFuture
// concluded from it. The raw response stays in OpenserveIntegrationLog
// (IntegrationLogId); this row keeps the facts in queryable columns so the
// checkout gate, the submission gate and Admin can all explain an answer
// without re-reading JSON:
//   • address identified? (AMID + Openserve's canonical LR_* address, DIST_M)
//   • Fibre available? (FTTH infrastructure + status)
//   • which products / speeds (Products rows)
//   • does the address match the customer's? (AddressMatch)
//   • is the package's mapped product eligible? (ProductEligibility)
//
// Written by the server only. A row is created per Openserve call (or per
// reuse of a very recent call for the same coordinates), never edited
// afterwards — except the explicit, audited Admin address acceptance.
public class OpenserveQualificationResult : BaseEntity
{
    /// <summary>The order this run was for (null for a pre-order coverage/checkout check that has not become an order). A plain indexed reference, not a foreign key: a run can be recorded before its order row is inserted.</summary>
    public Guid? OrderId { get; set; }

    public OpenserveQualificationPurpose Purpose { get; set; }

    /// <summary>The OpenserveIntegrationLogs row holding the raw request/response (null when no call was made).</summary>
    public Guid? IntegrationLogId { get; set; }

    public DateTime QualifiedAtUtc { get; set; }

    // ─── what was asked ─────────────────────────────────────────────
    public decimal? QueryLatitude { get; set; }
    public decimal? QueryLongitude { get; set; }
    public string? QueryAmid { get; set; }

    // ─── call outcome ───────────────────────────────────────────────
    /// <summary>Openserve answered with a readable OK response (errorCode 0).</summary>
    public bool CallSucceeded { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    // ─── ADDRESS_IDENTIFIED (AddressInfo, §3.1.1.2) ─────────────────
    public bool AddressIdentified { get; set; }
    public string? Amid { get; set; }
    public string? CanonicalAddress { get; set; }
    public string? StreetNumber { get; set; }
    public string? StreetName { get; set; }
    public string? StreetType { get; set; }
    public string? Suburb { get; set; }
    public string? Town { get; set; }
    public string? Province { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? AddressStatus { get; set; }

    /// <summary>DIST_M parsed to metres (e.g. "32.77 m" → 32.77). Supporting evidence only — never decides a match on its own.</summary>
    public decimal? DistanceMeters { get; set; }
    public string? DistanceText { get; set; }
    public string? MduVerification { get; set; }
    public string? AddressMessage { get; set; }
    public int BuildingCandidateCount { get; set; }

    /// <summary>The buildingInfo rows verbatim (JSON) — so an order created from this evidence gets the same MDU candidates without calling Openserve again.</summary>
    public string? BuildingCandidatesJson { get; set; }

    // ─── FIBRE (ftthInfrastructure / ftthOSInfo, §3.1.1.4) ──────────
    public OpenserveFibreAvailability FibreAvailability { get; set; }

    /// <summary>Every FTTH_Status returned, e.g. "Working" or "Working; pending (3rd_Party)".</summary>
    public string? FtthStatusSummary { get; set; }
    public int FtthInfrastructureCount { get; set; }
    public decimal? FibreMaxSpeedMbps { get; set; }

    /// <summary>ProductCodes on immediately-available FTTH, comma-separated — for quick querying.</summary>
    public string? AvailableProductCodes { get; set; }

    // Non-FTTH infrastructure, kept only as context for Admin.
    public string? EthernetProductCodes { get; set; }
    public string? FwaStatus { get; set; }

    // ─── address comparison (as evaluated for this run) ─────────────
    public string? CustomerAddress { get; set; }
    public OpenserveAddressMatch AddressMatch { get; set; }
    public string? AddressMatchDetail { get; set; }

    /// <summary>Admin confirmed, with a note, that Openserve's address IS the customer's property. Audited; only ever set on this row.</summary>
    public DateTime? AddressAcceptedAtUtc { get; set; }
    public Guid? AddressAcceptedByUserId { get; set; }
    public string? AddressAcceptanceNote { get; set; }

    // ─── package eligibility (as evaluated for this run) ────────────
    public Guid? ServicePackageId { get; set; }
    public string? MappingSku { get; set; }
    public string? MappingCapacity { get; set; }
    public string? MappingCapacityUom { get; set; }
    public OpenserveProductEligibility ProductEligibility { get; set; }
    public string? EligibilityReason { get; set; }

    public ICollection<OpenserveQualifiedProduct> Products { get; set; } = new List<OpenserveQualifiedProduct>();
}

// One product Openserve offered on one FTTH infrastructure entry
// (ftthProductInfo, §3.1.1.5). An infrastructure entry that listed no
// products is still recorded, with a null ProductCode, so its status is kept.
public class OpenserveQualifiedProduct : BaseEntity
{
    public Guid QualificationResultId { get; set; }
    public OpenserveQualificationResult? QualificationResult { get; set; }

    /// <summary>Position of the FTTH infrastructure entry in the response (0-based).</summary>
    public int InfrastructureIndex { get; set; }

    /// <summary>FTTH_Type as returned — null for Openserve's own network (the spec sample omits it), "3rd_Party" for partner networks.</summary>
    public string? InfrastructureType { get; set; }
    public string? FtthStatus { get; set; }
    public string? ServiceProviderId { get; set; }

    /// <summary>FTTH_Status is "Working" or "Available" — immediately available per §3.1.1.4.</summary>
    public bool IsImmediatelyAvailable { get; set; }
    public decimal? FibreMaxSpeedMbps { get; set; }

    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public string? UpstreamSpeed { get; set; }
    public string? DownstreamSpeed { get; set; }
    public decimal? UpstreamMbps { get; set; }
    public decimal? DownstreamMbps { get; set; }
}

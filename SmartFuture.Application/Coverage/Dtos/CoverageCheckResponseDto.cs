using SmartFuture.Shared.Enums.Coverage;

namespace SmartFuture.Application.Coverage.Dtos;

// Customer-safe normalised response. Mirrors the Openserve payload
// fields the website cares about (Status / max-speed / matched
// address / products) plus pre-computed friendly copy so every caller
// (Website, App, Portal) renders identical messaging without
// re-implementing the mapper.
public class CoverageCheckResponseDto
{
    public bool                     CoverageAvailable { get; set; }
    public string                   StatusLabel       { get; set; } = string.Empty;
    public string?                  RawStatus         { get; set; }
    public decimal?                 MaxSpeed          { get; set; }
    public string?                  MaxSpeedUnit      { get; set; }

    public string?                  MatchedAddress    { get; set; }
    public string?                  Suburb            { get; set; }
    public string?                  Town              { get; set; }
    public string?                  Province          { get; set; }
    public decimal?                 DistanceMeters    { get; set; }

    public decimal?                 Latitude          { get; set; }
    public decimal?                 Longitude         { get; set; }

    public List<CoverageProductDto> Products          { get; set; } = new();

    // Phase 47 — SmartFuture packages the visitor can actually order
    // at this address. Server-side matched so the website / mobile /
    // portal all see the same list without re-implementing the rules.
    // Empty when coverage is unavailable, when no Fibre packages are
    // active, or when none fit under the line's max speed.
    public List<ServicePackageCoverageDto> AvailablePackages { get; set; } = new();

    public string                   FriendlyTitle     { get; set; } = string.Empty;
    public string                   FriendlyMessage   { get; set; } = string.Empty;

    // Where the CoverageAvailable value came from. Default (Openserve)
    // means no admin rule tripped and the answer is Openserve's.
    // CoverageMapInclude / CoverageMapExclude mean an admin-configured
    // rule short-circuited the check before Openserve ran.
    public CoverageMatchSource      MatchSource       { get; set; } = CoverageMatchSource.Openserve;

    // Populated only when MatchSource is a CoverageMap* value. Handy
    // for admin support / debugging without leaking the rule's Notes.
    public Guid?                    MatchedRuleId     { get; set; }
    public string?                  MatchedRuleName   { get; set; }

    // Authenticated Openserve Product Qualification (MatchSource =
    // OpenserveQualification). AddressReviewRequired: Openserve resolved
    // the location to a different / unconfirmable property — the customer
    // must correct or re-pick the address. QualificationReference is an
    // opaque id of the server-side evidence; it carries no Openserve data.
    public bool                     AddressReviewRequired  { get; set; }
    public Guid?                    QualificationReference { get; set; }
}

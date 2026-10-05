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

    // What the authenticated qualification established, as one machine-
    // readable value: AddressUnresolved | NoAddressCandidates |
    // AddressReviewRequired | FtthUnavailable | ProductUnavailable |
    // Orderable (…). AddressVerificationRequired = the customer's exact
    // Openserve property isn't established, so Fibre at their address is
    // UNKNOWN (never shown as "no Fibre"). NearbyOpenserveAddresses lists
    // Openserve's address records near the pin (text + distance only — no
    // AMIDs or other internal identifiers).
    public string?                  FibreQualificationStatus    { get; set; }
    public bool                     AddressVerificationRequired { get; set; }
    public List<CoverageNearbyAddressDto> NearbyOpenserveAddresses { get; set; } = new();

    // No Openserve record matched the customer's address automatically, but
    // Openserve listed nearby service locations: the customer may choose the
    // one that corresponds to their property (POST api/coverage/openserve-
    // service-premises with QualificationReference + the candidate's Key).
    // Also true after a choice whose location can't be ordered, so they can
    // choose another. Their installation address never changes.
    public bool                     ServicePremisesSelectionRequired { get; set; }

    // The Openserve service location Fibre was evaluated for, once established
    // (matched automatically or chosen by the customer). Null otherwise.
    public CoverageServicePremisesDto? ServicePremises { get; set; }

    // Send this with the order / payment request (OpenserveServicePremisesReference)
    // when ServicePremises.Selection is "Customer": it is the customer's
    // confirmed choice, re-validated at checkout. Opaque — no Openserve data.
    public Guid?                    ServicePremisesReference { get; set; }
}

public class CoverageNearbyAddressDto
{
    // Opaque choice key for POST api/coverage/openserve-service-premises.
    public string?  Key                { get; set; }
    public string?  Address            { get; set; }
    public decimal? DistanceMeters     { get; set; }
    public bool     MatchesYourAddress { get; set; }
    // The record closest to the customer's pin (supporting information only — never chosen automatically).
    public bool     IsNearest          { get; set; }
    // The customer's current choice.
    public bool     IsSelected         { get; set; }
}

public class CoverageServicePremisesDto
{
    // Openserve's own address text for the service location.
    public string?  Address           { get; set; }
    public decimal? DistanceMeters    { get; set; }
    // "Automatic" (matched the customer's address) | "Customer" (chosen and confirmed by the customer).
    public string   Selection         { get; set; } = "Automatic";
    public bool     CustomerConfirmed { get; set; }
}

/// <summary>The customer's choice of Openserve service location from a coverage check's nearby records.</summary>
public class CoverageServicePremisesSelectionRequestDto
{
    // QualificationReference of the coverage check that listed the records.
    public Guid     VerificationReference { get; set; }
    // CoverageNearbyAddressDto.Key of the chosen record.
    public string?  CandidateKey          { get; set; }
    // The customer confirmed this Openserve record is the service location of their property. Required.
    public bool     Confirmed             { get; set; }

    // The customer's installation address (unchanged by the choice) — recorded with it.
    public string?  AddressLine1          { get; set; }
    public string?  Suburb                { get; set; }
    public string?  City                  { get; set; }
    public string?  Province              { get; set; }
}

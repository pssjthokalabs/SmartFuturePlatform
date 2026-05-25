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
}

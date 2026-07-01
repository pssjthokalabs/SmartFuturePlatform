namespace SmartFuture.Application.Coverage.Dtos;

// Input for /api/coverage/check. Either provide explicit coordinates
// (preferred — bypasses geocoding) or just an addressText string and
// the API will geocode server-side when a provider is configured.
//
// Structured address components (Suburb / City / Town / Province / …)
// are ALL optional. Clients that already parse Google Places results
// should forward as many as they have — the admin-configured Coverage
// Map rules can only match components the request actually carries.
// Older clients that send only `AddressText` still work; admins who
// want their rules to catch those must tick the FormattedAddress /
// FullAddress components (flagged "risky" in the admin UI).
public class CoverageCheckRequestDto
{
    // Legacy / free-text bag. Mapped to CoverageAddressMatchComponent.FullAddress.
    public string?  AddressText { get; set; }

    public decimal? Latitude    { get; set; }
    public decimal? Longitude   { get; set; }

    // Structured Google Places / manual address components. Any field
    // the client couldn't resolve stays null and is skipped during
    // rule matching.
    public string? AddressLine1     { get; set; }
    public string? AddressLine2     { get; set; }
    public string? StreetName       { get; set; }
    public string? Suburb           { get; set; }
    public string? City             { get; set; }
    public string? Town             { get; set; }
    public string? Province         { get; set; }
    public string? PostalCode       { get; set; }
    public string? Country          { get; set; }
    public string? FormattedAddress { get; set; }
    public string? PlaceName        { get; set; }
    public string? GooglePlaceId    { get; set; }
}

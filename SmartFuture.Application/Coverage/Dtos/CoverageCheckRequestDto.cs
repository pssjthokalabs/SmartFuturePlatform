namespace SmartFuture.Application.Coverage.Dtos;

// Input for /api/coverage/check. Either provide explicit coordinates
// (preferred — bypasses geocoding) or just an addressText string and
// the API will geocode server-side when a provider is configured.
public class CoverageCheckRequestDto
{
    public string?  AddressText { get; set; }
    public decimal? Latitude    { get; set; }
    public decimal? Longitude   { get; set; }
}

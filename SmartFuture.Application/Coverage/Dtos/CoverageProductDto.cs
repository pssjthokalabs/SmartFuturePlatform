namespace SmartFuture.Application.Coverage.Dtos;

// Normalised projection of one Openserve `ftthProductInfo` entry.
// Field names mirror the upstream payload to keep the mapper one-to-
// one, but null-safe and primitive-typed for the website to bind.
public class CoverageProductDto
{
    public string?  ProductName     { get; set; }
    public string?  ProductCode     { get; set; }
    public decimal? UpstreamSpeed   { get; set; }
    public decimal? DownstreamSpeed { get; set; }
    public string?  SpeedUnit       { get; set; }
}

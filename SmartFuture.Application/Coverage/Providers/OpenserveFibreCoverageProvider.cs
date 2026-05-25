using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage.Providers;

// Calls Openserve's public GIS coverage endpoint:
//   GET {BaseUrl}/gis/apps/api/ucmTechOSFibre?LAT=…&LON=…
//
// The endpoint is unauthenticated and returns a permissive JSON
// payload that mixes casings (FTTH_Status vs fibreMaxSpeed vs
// AddressInfo). The parser is deliberately tolerant — unknown
// fields are ignored, missing fields are mapped to nulls. We never
// surface the raw Openserve payload to the website.
//
// Timeout is governed by the named HttpClient registered in DI
// (see ServiceExtensions.AddCoverageServices) so a stuck upstream
// can't pin a request thread.
public class OpenserveFibreCoverageProvider : IFibreCoverageProvider
{
    public const string HttpClientName = "OpenserveCoverage";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenserveFibreCoverageProvider> _logger;

    public OpenserveFibreCoverageProvider(IHttpClientFactory httpClientFactory, IOptions<CoverageSettings> _, ILogger<OpenserveFibreCoverageProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    public async Task<Result<CoverageCheckResponseDto>> CheckAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var lat = latitude.ToString(CultureInfo.InvariantCulture);
        var lon = longitude.ToString(CultureInfo.InvariantCulture);
        var url = $"/gis/apps/api/ucmTechOSFibre?LAT={lat}&LON={lon}";

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Openserve coverage check returned {Status} for LAT={Lat} LON={Lon}.",
                    (int)response.StatusCode, lat, lon);
                return Result<CoverageCheckResponseDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Coverage lookup is temporarily unavailable. Please try again shortly.");
            }

            var payload = await response.Content.ReadFromJsonAsync<OpenservePayload>(JsonOptions, cancellationToken);
            if (payload is null)
            {
                return Result<CoverageCheckResponseDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Coverage lookup returned an unexpected response.");
            }

            return Result<CoverageCheckResponseDto>.Success(Map(payload, latitude, longitude));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Openserve coverage check timed out for LAT={Lat} LON={Lon}.", lat, lon);
            return Result<CoverageCheckResponseDto>.Failure(
                ErrorCodes.EXCEPTION,
                "Coverage lookup timed out. Please try again shortly.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Openserve coverage HTTP failure for LAT={Lat} LON={Lon}.", lat, lon);
            return Result<CoverageCheckResponseDto>.Failure(
                ErrorCodes.EXCEPTION,
                "Coverage lookup is temporarily unavailable. Please try again shortly.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Openserve coverage payload parse error for LAT={Lat} LON={Lon}.", lat, lon);
            return Result<CoverageCheckResponseDto>.Failure(
                ErrorCodes.EXCEPTION,
                "Coverage lookup returned an unexpected response.");
        }
    }

    private static CoverageCheckResponseDto Map(OpenservePayload payload, decimal lat, decimal lon)
    {
        var status        = (payload.FTTH_Status ?? payload.ftth_status ?? string.Empty).Trim();
        var isAvailable   = string.Equals(status, "Working", StringComparison.OrdinalIgnoreCase);
        var statusLabel   = isAvailable ? "Available" : string.IsNullOrEmpty(status) ? "Unknown" : "Unavailable";
        var addressInfo   = payload.AddressInfo ?? payload.addressInfo;
        var matchedAddr   = ComposeMatchedAddress(addressInfo);
        var products      = MapProducts(payload.ftthProductInfo ?? payload.FTTHProductInfo);

        var (title, message) = isAvailable
            ? ("Good news — fibre coverage is available at this address.",
               "We've matched your address to our fibre network. Browse the packages below to find the right fit.")
            : ("Coverage isn't available here yet.",
               "We couldn't confirm fibre coverage at this address. Our team can help you explore alternatives or notify you once coverage expands.");

        return new CoverageCheckResponseDto
        {
            CoverageAvailable = isAvailable,
            StatusLabel       = statusLabel,
            RawStatus         = string.IsNullOrEmpty(status) ? null : status,
            MaxSpeed          = payload.fibreMaxSpeed ?? payload.FibreMaxSpeed,
            MaxSpeedUnit      = payload.fibreMaxSpeedUnit ?? payload.FibreMaxSpeedUnit,
            MatchedAddress    = matchedAddr,
            Suburb            = addressInfo?.Suburb       ?? addressInfo?.suburb,
            Town              = addressInfo?.Town         ?? addressInfo?.town ?? addressInfo?.City ?? addressInfo?.city,
            Province          = addressInfo?.Province     ?? addressInfo?.province,
            DistanceMeters    = addressInfo?.Distance     ?? addressInfo?.distance,
            Latitude          = lat,
            Longitude         = lon,
            Products          = products,
            FriendlyTitle     = title,
            FriendlyMessage   = message
        };
    }

    private static List<CoverageProductDto> MapProducts(List<OpenserveProduct>? input)
    {
        if (input is null || input.Count == 0) return new List<CoverageProductDto>();
        var output = new List<CoverageProductDto>(input.Count);
        foreach (var p in input)
        {
            if (p is null) continue;
            output.Add(new CoverageProductDto
            {
                ProductName     = p.productName     ?? p.ProductName,
                ProductCode     = p.productCode     ?? p.ProductCode,
                UpstreamSpeed   = p.upstreamSpeed   ?? p.UpstreamSpeed,
                DownstreamSpeed = p.downstreamSpeed ?? p.DownstreamSpeed,
                SpeedUnit       = p.speedUnit       ?? p.SpeedUnit
            });
        }
        return output;
    }

    private static string? ComposeMatchedAddress(OpenserveAddressInfo? info)
    {
        if (info is null) return null;
        var line1   = info.Address     ?? info.address     ?? info.StreetAddress ?? info.streetAddress;
        var suburb  = info.Suburb      ?? info.suburb;
        var town    = info.Town        ?? info.town ?? info.City ?? info.city;
        var parts   = new List<string>();
        if (!string.IsNullOrWhiteSpace(line1))  parts.Add(line1.Trim());
        if (!string.IsNullOrWhiteSpace(suburb)) parts.Add(suburb.Trim());
        if (!string.IsNullOrWhiteSpace(town))   parts.Add(town.Trim());
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // Permissive deserialisation surface — Openserve mixes casings,
    // so each property has both spellings. Unknown fields stay
    // ignored thanks to the default JsonSerializer behaviour.
    private sealed class OpenservePayload
    {
        public string?                 FTTH_Status        { get; set; }
        public string?                 ftth_status        { get; set; }
        public decimal?                fibreMaxSpeed      { get; set; }
        public decimal?                FibreMaxSpeed      { get; set; }
        public string?                 fibreMaxSpeedUnit  { get; set; }
        public string?                 FibreMaxSpeedUnit  { get; set; }
        public OpenserveAddressInfo?   AddressInfo        { get; set; }
        public OpenserveAddressInfo?   addressInfo        { get; set; }
        public List<OpenserveProduct>? ftthProductInfo    { get; set; }
        public List<OpenserveProduct>? FTTHProductInfo    { get; set; }
    }

    private sealed class OpenserveAddressInfo
    {
        public string?  Address       { get; set; }
        public string?  address       { get; set; }
        public string?  StreetAddress { get; set; }
        public string?  streetAddress { get; set; }
        public string?  Suburb        { get; set; }
        public string?  suburb        { get; set; }
        public string?  Town          { get; set; }
        public string?  town          { get; set; }
        public string?  City          { get; set; }
        public string?  city          { get; set; }
        public string?  Province      { get; set; }
        public string?  province      { get; set; }
        public decimal? Distance      { get; set; }
        public decimal? distance      { get; set; }
    }

    private sealed class OpenserveProduct
    {
        public string?  productName     { get; set; }
        public string?  ProductName     { get; set; }
        public string?  productCode     { get; set; }
        public string?  ProductCode     { get; set; }
        public decimal? upstreamSpeed   { get; set; }
        public decimal? UpstreamSpeed   { get; set; }
        public decimal? downstreamSpeed { get; set; }
        public decimal? DownstreamSpeed { get; set; }
        public string?  speedUnit       { get; set; }
        public string?  SpeedUnit       { get; set; }
    }
}

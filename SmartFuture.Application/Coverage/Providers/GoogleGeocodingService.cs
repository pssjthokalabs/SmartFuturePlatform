using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage.Providers;

// Google Maps Geocoding API wrapper. Activated only when
// CoverageSettings:GoogleMaps:ApiKey is present. The key is read at
// call time so a key rotation via env var picks up without an app
// restart.
//
//   GET https://maps.googleapis.com/maps/api/geocode/json
//       ?address={url-encoded}&key={apiKey}[&region={cc}]
//
// Returns NOT_FOUND when Google's `status` is ZERO_RESULTS or no
// `results[0].geometry.location` is present; EXCEPTION on any other
// status / network / parse problem. Never throws.
public class GoogleGeocodingService : IGeocodingService
{
    public const string HttpClientName = "GoogleGeocoding";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory                _httpClientFactory;
    private readonly IOptionsMonitor<CoverageSettings> _settings;
    private readonly ILogger<GoogleGeocodingService>   _logger;

    public GoogleGeocodingService(IHttpClientFactory httpClientFactory, IOptionsMonitor<CoverageSettings> settings, ILogger<GoogleGeocodingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings          = settings;
        _logger            = logger;
    }

    public async Task<Result<GeocodeResult>> GeocodeAsync(string addressText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(addressText))
            return Result<GeocodeResult>.Failure(ErrorCodes.VALIDATION_ERROR, "Address is required.");

        var current = _settings.CurrentValue;
        if (!current.GoogleMaps.IsConfigured)
        {
            return Result<GeocodeResult>.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED,
                "Address autocomplete isn't configured. Please pick the suggested address or supply coordinates.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var query  = HttpUtility.UrlEncode(addressText.Trim());
        var url    = $"/maps/api/geocode/json?address={query}&key={current.GoogleMaps.ApiKey}";
        if (!string.IsNullOrWhiteSpace(current.GoogleMaps.Region))
            url += $"&region={current.GoogleMaps.Region}";

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google geocoding returned {Status} for address '{Address}'.",
                    (int)response.StatusCode, addressText);
                return Result<GeocodeResult>.Failure(
                    ErrorCodes.EXCEPTION, "Address lookup is temporarily unavailable.");
            }

            var payload = await response.Content.ReadFromJsonAsync<GooglePayload>(JsonOptions, cancellationToken);
            if (payload is null)
                return Result<GeocodeResult>.Failure(ErrorCodes.EXCEPTION, "Address lookup returned an unexpected response.");

            if (!string.Equals(payload.status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(payload.status, "ZERO_RESULTS", StringComparison.OrdinalIgnoreCase))
                    return Result<GeocodeResult>.Failure(ErrorCodes.NOT_FOUND, "We couldn't match that address.");
                _logger.LogWarning("Google geocoding non-OK status '{Status}' for '{Address}'.", payload.status, addressText);
                return Result<GeocodeResult>.Failure(ErrorCodes.EXCEPTION, "Address lookup is temporarily unavailable.");
            }

            var first = payload.results?.FirstOrDefault();
            var loc   = first?.geometry?.location;
            if (first is null || loc is null)
                return Result<GeocodeResult>.Failure(ErrorCodes.NOT_FOUND, "We couldn't match that address.");

            return Result<GeocodeResult>.Success(new GeocodeResult(
                (decimal)loc.lat,
                (decimal)loc.lng,
                first.formatted_address));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Google geocoding timed out for '{Address}'.", addressText);
            return Result<GeocodeResult>.Failure(ErrorCodes.EXCEPTION, "Address lookup timed out. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google geocoding HTTP failure for '{Address}'.", addressText);
            return Result<GeocodeResult>.Failure(ErrorCodes.EXCEPTION, "Address lookup is temporarily unavailable.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Google geocoding parse error for '{Address}'.", addressText);
            return Result<GeocodeResult>.Failure(ErrorCodes.EXCEPTION, "Address lookup returned an unexpected response.");
        }
    }

    private sealed class GooglePayload
    {
        public string?              status  { get; set; }
        public List<GoogleResult>?  results { get; set; }
    }

    private sealed class GoogleResult
    {
        public string?          formatted_address { get; set; }
        public GoogleGeometry?  geometry          { get; set; }
    }

    private sealed class GoogleGeometry
    {
        public GoogleLocation? location { get; set; }
    }

    private sealed class GoogleLocation
    {
        public double lat { get; set; }
        public double lng { get; set; }
    }
}

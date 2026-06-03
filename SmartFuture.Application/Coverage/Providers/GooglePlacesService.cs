using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage.Providers;

// Server-side proxy to Google Places (Autocomplete + Details). Uses the
// same CoverageSettings:GoogleMaps:ApiKey that GoogleGeocodingService
// reads — single key, single restriction policy in the Google Cloud
// Console.
//
//   GET /maps/api/place/autocomplete/json?input=&key=&types=address&components=country:za[&sessiontoken=]
//   GET /maps/api/place/details/json?place_id=&key=&fields=formatted_address,geometry,address_components,place_id[&sessiontoken=]
//
// We intentionally re-use the existing "GoogleGeocoding" named HttpClient
// (its BaseAddress is `https://maps.googleapis.com`) so DI is identical
// to GoogleGeocodingService and we don't have to add another factory
// registration.
public class GooglePlacesService : IGooglePlacesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory                _httpClientFactory;
    private readonly IOptionsMonitor<CoverageSettings> _settings;
    private readonly ILogger<GooglePlacesService>      _logger;

    public GooglePlacesService(IHttpClientFactory httpClientFactory, IOptionsMonitor<CoverageSettings> settings, ILogger<GooglePlacesService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings          = settings;
        _logger            = logger;
    }

    public async Task<Result<PlaceAutocompleteResult>> AutocompleteAsync(string query, string? sessionToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 3)
            return Result<PlaceAutocompleteResult>.Failure(ErrorCodes.VALIDATION_ERROR, "Query must be at least 3 characters.");

        var current = _settings.CurrentValue;
        if (!current.GoogleMaps.IsConfigured)
        {
            return Result<PlaceAutocompleteResult>.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED,
                "Address autocomplete isn't configured on the server.");
        }

        var client = _httpClientFactory.CreateClient(GoogleGeocodingService.HttpClientName);
        var url    = $"/maps/api/place/autocomplete/json?input={HttpUtility.UrlEncode(query.Trim())}&key={current.GoogleMaps.ApiKey}&types=address&components=country:za";
        if (!string.IsNullOrWhiteSpace(sessionToken))
            url += $"&sessiontoken={HttpUtility.UrlEncode(sessionToken)}";

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Places autocomplete returned {Status} for query length {Length}.",
                    (int)response.StatusCode, query.Length);
                return Result<PlaceAutocompleteResult>.Failure(
                    ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup is temporarily unavailable.");
            }

            var payload = await response.Content.ReadFromJsonAsync<AutocompletePayload>(JsonOptions, cancellationToken);
            if (payload is null)
                return Result<PlaceAutocompleteResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup returned an unexpected response.");

            if (string.Equals(payload.status, "ZERO_RESULTS", StringComparison.OrdinalIgnoreCase))
                return Result<PlaceAutocompleteResult>.Success(new PlaceAutocompleteResult(Array.Empty<PlacePrediction>()));

            if (!string.Equals(payload.status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Google Places autocomplete non-OK '{Status}' (errorMessage='{ErrorMessage}').",
                    payload.status, payload.error_message ?? string.Empty);
                return Result<PlaceAutocompleteResult>.Failure(
                    ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup is temporarily unavailable.");
            }

            var predictions = (payload.predictions ?? new List<AutocompletePrediction>())
                .Where(p => !string.IsNullOrWhiteSpace(p.place_id))
                .Take(5)
                .Select(p => new PlacePrediction(
                    p.place_id!,
                    p.description ?? string.Empty,
                    p.structured_formatting?.main_text,
                    p.structured_formatting?.secondary_text))
                .ToList();

            return Result<PlaceAutocompleteResult>.Success(new PlaceAutocompleteResult(predictions));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<PlaceAutocompleteResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup timed out. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google Places autocomplete HTTP failure.");
            return Result<PlaceAutocompleteResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup is temporarily unavailable.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Google Places autocomplete parse error.");
            return Result<PlaceAutocompleteResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup returned an unexpected response.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Places autocomplete unexpected failure.");
            return Result<PlaceAutocompleteResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup failed. Please try again.");
        }
    }

    public async Task<Result<PlaceDetailsResult>> GetPlaceDetailsAsync(string placeId, string? sessionToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(placeId))
            return Result<PlaceDetailsResult>.Failure(ErrorCodes.VALIDATION_ERROR, "placeId is required.");

        var current = _settings.CurrentValue;
        if (!current.GoogleMaps.IsConfigured)
        {
            return Result<PlaceDetailsResult>.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED,
                "Address autocomplete isn't configured on the server.");
        }

        var client = _httpClientFactory.CreateClient(GoogleGeocodingService.HttpClientName);
        var url    = $"/maps/api/place/details/json?place_id={HttpUtility.UrlEncode(placeId)}&key={current.GoogleMaps.ApiKey}&fields=formatted_address,geometry,address_components,place_id";
        if (!string.IsNullOrWhiteSpace(sessionToken))
            url += $"&sessiontoken={HttpUtility.UrlEncode(sessionToken)}";

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Places details returned {Status}.", (int)response.StatusCode);
                return Result<PlaceDetailsResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup is temporarily unavailable.");
            }

            var payload = await response.Content.ReadFromJsonAsync<DetailsPayload>(JsonOptions, cancellationToken);
            if (payload is null || payload.result is null)
                return Result<PlaceDetailsResult>.Failure(ErrorCodes.NOT_FOUND, "We couldn't find that place.");

            if (!string.Equals(payload.status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(payload.status, "ZERO_RESULTS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(payload.status, "NOT_FOUND", StringComparison.OrdinalIgnoreCase))
                    return Result<PlaceDetailsResult>.Failure(ErrorCodes.NOT_FOUND, "We couldn't find that place.");
                _logger.LogWarning("Google Places details non-OK '{Status}'.", payload.status);
                return Result<PlaceDetailsResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup is temporarily unavailable.");
            }

            var components = payload.result.address_components ?? new List<AddressComponent>();
            string? Long(string type)
                => components.FirstOrDefault(c => c.types != null && c.types.Contains(type, StringComparer.OrdinalIgnoreCase))?.long_name;
            string? Short(string type)
                => components.FirstOrDefault(c => c.types != null && c.types.Contains(type, StringComparer.OrdinalIgnoreCase))?.short_name;

            var streetNumber = Long("street_number");
            var route        = Long("route");
            var addressLine1 = string.IsNullOrWhiteSpace(streetNumber)
                ? route
                : string.IsNullOrWhiteSpace(route) ? streetNumber : $"{streetNumber} {route}";

            return Result<PlaceDetailsResult>.Success(new PlaceDetailsResult(
                payload.result.place_id ?? placeId,
                payload.result.formatted_address,
                payload.result.geometry?.location?.lat is { } lat ? (decimal)lat : null,
                payload.result.geometry?.location?.lng is { } lng ? (decimal)lng : null,
                addressLine1,
                Long("sublocality_level_1") ?? Long("sublocality") ?? Long("neighborhood"),
                Long("locality") ?? Long("administrative_area_level_2") ?? Long("postal_town"),
                Long("administrative_area_level_1"),
                Long("postal_code"),
                Long("country") ?? Short("country")));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<PlaceDetailsResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup timed out. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google Places details HTTP failure.");
            return Result<PlaceDetailsResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup is temporarily unavailable.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Google Places details parse error.");
            return Result<PlaceDetailsResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup returned an unexpected response.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Places details unexpected failure.");
            return Result<PlaceDetailsResult>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, "Address lookup failed. Please try again.");
        }
    }

    private sealed class AutocompletePayload
    {
        public string?                       status         { get; set; }
        public List<AutocompletePrediction>? predictions    { get; set; }
        public string?                       error_message  { get; set; }
    }

    private sealed class AutocompletePrediction
    {
        public string?                  place_id              { get; set; }
        public string?                  description           { get; set; }
        public StructuredFormatting?    structured_formatting { get; set; }
    }

    private sealed class StructuredFormatting
    {
        public string? main_text      { get; set; }
        public string? secondary_text { get; set; }
    }

    private sealed class DetailsPayload
    {
        public string?       status { get; set; }
        public DetailsResult? result { get; set; }
    }

    private sealed class DetailsResult
    {
        public string?                place_id           { get; set; }
        public string?                formatted_address  { get; set; }
        public DetailsGeometry?       geometry           { get; set; }
        public List<AddressComponent>? address_components { get; set; }
    }

    private sealed class DetailsGeometry
    {
        public DetailsLocation? location { get; set; }
    }

    private sealed class DetailsLocation
    {
        public double lat { get; set; }
        public double lng { get; set; }
    }

    private sealed class AddressComponent
    {
        public string?       long_name  { get; set; }
        public string?       short_name { get; set; }
        public List<string>? types      { get; set; }
    }
}

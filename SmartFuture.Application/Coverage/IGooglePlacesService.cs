using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage;

// Server-side Google Places proxy. The mobile app calls these endpoints
// when the native Places REST path is unreachable (Huawei / no Google
// Play Services / restricted networks), so the API key never has to
// leave the server and the device doesn't need GMS.
//
// Failure modes match GoogleGeocodingService:
//   PROVIDER_NOT_CONFIGURED  — no CoverageSettings:GoogleMaps:ApiKey
//   NOT_FOUND                — provider responded but no match
//   VALIDATION_ERROR         — caller-side input problem (empty query)
//   UPSTREAM_UNAVAILABLE     — network / non-OK upstream / parse fault
public interface IGooglePlacesService
{
    Task<Result<PlaceAutocompleteResult>> AutocompleteAsync(string query, string? sessionToken, CancellationToken cancellationToken = default);

    Task<Result<PlaceDetailsResult>> GetPlaceDetailsAsync(string placeId, string? sessionToken, CancellationToken cancellationToken = default);
}

public record PlaceAutocompleteResult(IReadOnlyList<PlacePrediction> Predictions);

public record PlacePrediction(
    string PlaceId,
    string Description,
    string? MainText,
    string? SecondaryText);

public record PlaceDetailsResult(
    string  PlaceId,
    string? FormattedAddress,
    decimal? Latitude,
    decimal? Longitude,
    string? AddressLine1,
    string? Suburb,
    string? City,
    string? Province,
    string? PostalCode,
    string? Country);

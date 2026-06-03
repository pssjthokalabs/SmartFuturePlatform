using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Coverage;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

// Server-side address-lookup proxy for the SmartFutureApp.
//
// SmartFutureApp previously called Google Places REST directly from the
// device. That breaks on Huawei P40 Lite / phones running GBox or any
// AOSP build without Google Play Services — Google's mobile SDK refuses
// to issue the lookup, the REST URL is blocked by the locked-down key,
// or the network rewrites the response. The fallback used to be "user
// types the whole address manually" which loses the structured
// addressLine1 / city / province / postalCode the order-intent payload
// needs.
//
// These endpoints expose the same Google APIs through the API server so:
//   1. The key never leaves the server (already locked in Google Cloud
//      Console for the SmartFuture project; mobile cannot expose it).
//   2. Huawei / restricted-network devices get suggestions via the
//      regular SmartFuture API host instead of maps.googleapis.com.
//   3. Web autocomplete (which is CORS-blocked against
//      maps.googleapis.com) can also fall back to this endpoint.
//
// All three are anonymous: the marketing site, register flow, AND
// authenticated app screens all need them, and they're stateless reads
// rate-limited by the upstream Google quota.
[Route("api/location")]
public class LocationController : BaseController
{
    private readonly IGooglePlacesService          _places;
    private readonly IGeocodingService             _geocoding;
    private readonly ILogger<LocationController>   _logger;

    public LocationController(
        IGooglePlacesService places,
        IGeocodingService geocoding,
        ILogger<LocationController> logger)
    {
        _places    = places;
        _geocoding = geocoding;
        _logger    = logger;
    }

    // GET /api/location/autocomplete?query=12+main+st&sessionToken=...
    //
    // Returns up to 5 PlacePrediction rows for the typed query, locked
    // to South Africa + address types. SessionToken is optional and
    // forwarded to Google for billing-grouping.
    [HttpGet("autocomplete")]
    [AllowAnonymous]
    public async Task<IActionResult> Autocomplete(
        [FromQuery] string? query,
        [FromQuery(Name = "sessionToken")] string? sessionToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToActionResult(await _places.AutocompleteAsync(query ?? string.Empty, sessionToken, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest, new
            {
                IsSuccess = false,
                Code      = ErrorCodes.EXCEPTION,
                Message   = "Request cancelled."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Location autocomplete controller-level failure.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                IsSuccess = false,
                Code      = ErrorCodes.UPSTREAM_UNAVAILABLE,
                Message   = "Address lookup failed unexpectedly. Please try again shortly."
            });
        }
    }

    // GET /api/location/place-details?placeId=ChIJ...&sessionToken=...
    //
    // Resolves the chosen prediction into a structured
    // address-line / suburb / city / province / postalCode / country
    // plus lat/lng — identical to what AddressAutocomplete.tsx used to
    // parse client-side from Google's address_components.
    [HttpGet("place-details")]
    [AllowAnonymous]
    public async Task<IActionResult> PlaceDetails(
        [FromQuery] string? placeId,
        [FromQuery(Name = "sessionToken")] string? sessionToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToActionResult(await _places.GetPlaceDetailsAsync(placeId ?? string.Empty, sessionToken, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest, new
            {
                IsSuccess = false,
                Code      = ErrorCodes.EXCEPTION,
                Message   = "Request cancelled."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Location place-details controller-level failure.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                IsSuccess = false,
                Code      = ErrorCodes.UPSTREAM_UNAVAILABLE,
                Message   = "Address lookup failed unexpectedly. Please try again shortly."
            });
        }
    }

    // GET /api/location/geocode?address=12+Main+St+Sandton
    //
    // Manual-address fallback: the customer typed something Places
    // autocomplete didn't surface (new estate, off-grid unit) and we
    // still need lat/lng + a server-validated formatted address before
    // letting them continue to payment. Same GoogleGeocodingService the
    // coverage endpoint uses.
    [HttpGet("geocode")]
    [AllowAnonymous]
    public async Task<IActionResult> Geocode(
        [FromQuery] string? address,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return ToActionResult(
                Result<GeocodeResult>.Failure(ErrorCodes.VALIDATION_ERROR, "address is required."));
        }

        try
        {
            return ToActionResult(await _geocoding.GeocodeAsync(address, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest, new
            {
                IsSuccess = false,
                Code      = ErrorCodes.EXCEPTION,
                Message   = "Request cancelled."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Location geocode controller-level failure.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                IsSuccess = false,
                Code      = ErrorCodes.UPSTREAM_UNAVAILABLE,
                Message   = "Address lookup failed unexpectedly. Please try again shortly."
            });
        }
    }
}

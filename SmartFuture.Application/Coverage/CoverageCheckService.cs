using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Coverage.Providers;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage;

// Orchestrates an end-to-end coverage check:
//   1. If lat/lon arrived in the request, skip geocoding and call
//      the fibre provider directly.
//   2. Otherwise geocode `AddressText`. If geocoding is unavailable
//      (no key, NOT_FOUND, etc.) bubble that failure to the caller
//      so the UI can prompt for coordinates / a different address.
//   3. Stamp the matched/formatted address onto the response when
//      the provider didn't already supply one.
//
// The service has zero knowledge of Openserve specifics — it talks
// only to IFibreCoverageProvider so we can swap to a different
// upstream (Vumatel, Frogfoot) without touching this orchestrator.
public class CoverageCheckService : ICoverageCheckService
{
    private readonly IGeocodingService             _geocoding;
    private readonly IFibreCoverageProvider        _fibreProvider;
    private readonly IHostEnvironment              _environment;
    private readonly ILogger<CoverageCheckService> _logger;

    public CoverageCheckService(IGeocodingService geocoding, IFibreCoverageProvider fibreProvider, IHostEnvironment environment, ILogger<CoverageCheckService> logger)
    {
        _geocoding     = geocoding;
        _fibreProvider = fibreProvider;
        _environment   = environment;
        _logger        = logger;
    }

    public async Task<Result<CoverageCheckResponseDto>> CheckAsync(CoverageCheckRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return Result<CoverageCheckResponseDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

        var hasCoords  = request.Latitude.HasValue && request.Longitude.HasValue;
        var hasAddress = !string.IsNullOrWhiteSpace(request.AddressText);
        if (!hasCoords && !hasAddress)
        {
            return Result<CoverageCheckResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Provide an address or latitude/longitude.");
        }

        decimal lat, lon;
        string? formattedAddress = null;
        var isDev = _environment.IsDevelopment();

        if (hasCoords)
        {
            lat = request.Latitude!.Value;
            lon = request.Longitude!.Value;
            if (lat < -90m  || lat > 90m)  return Result<CoverageCheckResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Latitude must be between -90 and 90.");
            if (lon < -180m || lon > 180m) return Result<CoverageCheckResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Longitude must be between -180 and 180.");
            if (isDev)
                _logger.LogInformation("[Coverage] Skipping geocoding; using supplied coords LAT={Lat} LON={Lon}.", lat, lon);
        }
        else
        {
            if (isDev)
                _logger.LogInformation("[Coverage] Geocoding address '{Address}'.", request.AddressText);

            var geocode = await _geocoding.GeocodeAsync(request.AddressText!, cancellationToken);
            if (!geocode.IsSuccess || geocode.Data is null)
            {
                if (isDev)
                    _logger.LogInformation("[Coverage] Geocoding FAILED for '{Address}': {Code} {Message}", request.AddressText, geocode.Code, geocode.Message);
                return Result<CoverageCheckResponseDto>.Failure(geocode.Code ?? ErrorCodes.EXCEPTION, geocode.Message);
            }

            lat              = geocode.Data.Latitude;
            lon              = geocode.Data.Longitude;
            formattedAddress = geocode.Data.FormattedAddress;

            if (isDev)
                _logger.LogInformation("[Coverage] Geocoded '{Address}' -> LAT={Lat} LON={Lon} formatted='{Formatted}'.",
                    request.AddressText, lat, lon, formattedAddress);
        }

        var coverage = await _fibreProvider.CheckAsync(lat, lon, cancellationToken);
        if (!coverage.IsSuccess || coverage.Data is null)
        {
            if (isDev)
                _logger.LogInformation("[Coverage] Openserve lookup FAILED for LAT={Lat} LON={Lon}: {Code} {Message}", lat, lon, coverage.Code, coverage.Message);
            return Result<CoverageCheckResponseDto>.Failure(coverage.Code ?? ErrorCodes.EXCEPTION, coverage.Message);
        }

        var dto = coverage.Data;
        // Prefer Openserve's matched address when present; fall back
        // to the geocoder's formatted address; finally the caller's
        // raw input so the UI always has something to display.
        if (string.IsNullOrWhiteSpace(dto.MatchedAddress))
            dto.MatchedAddress = formattedAddress ?? request.AddressText?.Trim();

        if (isDev)
        {
            _logger.LogInformation(
                "[Coverage] Openserve responded: status='{Status}' available={Available} maxSpeed={MaxSpeed}{Unit} matchedAddress='{Matched}' products={Products}",
                dto.RawStatus ?? dto.StatusLabel, dto.CoverageAvailable, dto.MaxSpeed, dto.MaxSpeedUnit, dto.MatchedAddress, dto.Products.Count);
        }
        else
        {
            // Production: keep a single Information line so operators
            // can correlate without leaking address details into logs.
            _logger.LogInformation(
                "Coverage check completed: available={Available} status='{Status}' products={Products}",
                dto.CoverageAvailable, dto.RawStatus ?? dto.StatusLabel, dto.Products.Count);
        }

        return Result<CoverageCheckResponseDto>.Success(dto);
    }
}

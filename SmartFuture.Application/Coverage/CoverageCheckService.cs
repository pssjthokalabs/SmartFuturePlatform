using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Coverage.Providers;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.ServicePackages;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Shared.Enums.Coverage;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
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
// While the Openserve integration can qualify (IOpenserveQualificationService
// .CanQualify), the AUTHENTICATED Product Qualification is the Fibre
// authority: the public GIS lookup is not consulted, admin Include rules
// don't bypass it, and AvailablePackages lists only the packages whose
// Openserve mapping qualification says is orderable at the address (Fibre
// available, product/speed offered, address confirmed). Otherwise the
// legacy IFibreCoverageProvider path below applies unchanged.
public class CoverageCheckService : ICoverageCheckService
{
    private readonly IGeocodingService             _geocoding;
    private readonly IFibreCoverageProvider        _fibreProvider;
    private readonly IServicePackageService        _servicePackages;
    private readonly ICoverageMapRuleService       _coverageMap;
    private readonly IHostEnvironment              _environment;
    private readonly ILogger<CoverageCheckService> _logger;
    private readonly IOpenserveQualificationService? _qualification;

    public CoverageCheckService(IGeocodingService geocoding, IFibreCoverageProvider fibreProvider, IServicePackageService servicePackages,
        ICoverageMapRuleService coverageMap, IHostEnvironment environment, ILogger<CoverageCheckService> logger, IOpenserveQualificationService? qualification = null)
    {
        _geocoding       = geocoding;
        _fibreProvider   = fibreProvider;
        _servicePackages = servicePackages;
        _coverageMap     = coverageMap;
        _environment     = environment;
        _logger          = logger;
        _qualification   = qualification;
    }

    // Matches "250 Mbps", "1 Gbps", "1000", " 500 mbit/s ". Returns
    // the speed normalised to Mbps so we can compare apples to apples.
    // Returns null when nothing numeric can be extracted — the caller
    // simply skips that input.
    private static readonly Regex SpeedRegex = new(
        @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>kbps|mbps|mbit/s|mbit|gbps|gb/s)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static decimal? ParseMbps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = SpeedRegex.Match(text);
        if (!match.Success) return null;
        if (!decimal.TryParse(match.Groups["value"].Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        var unit = match.Groups["unit"].Value?.ToLowerInvariant() ?? "";
        return unit switch
        {
            "kbps"            => Math.Round(value / 1000m, 3),
            "gbps" or "gb/s"  => value * 1000m,
            _                 => value // mbps / mbit / mbit/s / unitless → assume Mbps
        };
    }

    public async Task<Result<CoverageCheckResponseDto>> CheckAsync(CoverageCheckRequestDto request, CancellationToken cancellationToken = default)
    {
        // Top-level try/catch — every known failure mode already
        // returns Result.Failure with a friendly Code/Message, but a
        // bug here (DI miswire, NRE, etc.) must NOT escape as a
        // generic 500. Map anything unforeseen to UPSTREAM_UNAVAILABLE
        // so the caller still sees a 502-shaped Result envelope.
        try
        {
            return await CheckInternalAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Coverage] Unhandled exception in CheckAsync.");
            return Result<CoverageCheckResponseDto>.Failure(
                ErrorCodes.UPSTREAM_UNAVAILABLE,
                "Coverage check failed unexpectedly. Please try again shortly.");
        }
    }

    // South Africa bounding box. Generous so border-towns and offshore
    // service addresses still pass while obviously-wrong coords (Gulf
    // of Guinea, Europe, Asia, etc.) are rejected before we burn an
    // Openserve round-trip on them.
    //   Lat:  -35.0 (Cape Agulhas + buffer) → -21.5 (north of Musina)
    //   Lon:  16.0  (west of Cape Town)     →  33.0 (east of Mozambique border)
    private const decimal SaLatMin = -35.0m;
    private const decimal SaLatMax = -21.5m;
    private const decimal SaLonMin =  16.0m;
    private const decimal SaLonMax =  33.0m;

    private async Task<Result<CoverageCheckResponseDto>> CheckInternalAsync(CoverageCheckRequestDto request, CancellationToken cancellationToken)
    {
        if (request is null)
            return Result<CoverageCheckResponseDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

        // 0,0 is the canonical "client forgot to set coordinates"
        // sentinel — it's a valid point in the Gulf of Guinea but
        // never what a SmartFuture customer means. Treat as missing
        // so we fall back to addressText geocoding when available.
        var bothZeroCoords = request.Latitude == 0m && request.Longitude == 0m;
        var hasCoords      = request.Latitude.HasValue && request.Longitude.HasValue && !bothZeroCoords;
        var hasAddress     = !string.IsNullOrWhiteSpace(request.AddressText);
        var hasStructured  = !string.IsNullOrWhiteSpace(request.Suburb)
                           || !string.IsNullOrWhiteSpace(request.City)
                           || !string.IsNullOrWhiteSpace(request.Town)
                           || !string.IsNullOrWhiteSpace(request.Province)
                           || !string.IsNullOrWhiteSpace(request.FormattedAddress)
                           || !string.IsNullOrWhiteSpace(request.PlaceName)
                           || !string.IsNullOrWhiteSpace(request.AddressLine1)
                           || !string.IsNullOrWhiteSpace(request.AddressLine2)
                           || !string.IsNullOrWhiteSpace(request.StreetName)
                           || !string.IsNullOrWhiteSpace(request.PostalCode)
                           || !string.IsNullOrWhiteSpace(request.Country);
        if (!hasCoords && !hasAddress && !hasStructured)
        {
            return Result<CoverageCheckResponseDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                bothZeroCoords
                    ? "Latitude/longitude of 0,0 isn't a valid location. Provide a real address or coordinates."
                    : "Provide an address or latitude/longitude.");
        }

        // Consult the admin-configured Coverage Map BEFORE calling
        // Openserve. Excludes are checked before Includes; if either
        // trips we short-circuit here with an admin-authored answer.
        // The evaluator is safe on any request shape — it just skips
        // components the caller didn't send.
        var mapHit = await _coverageMap.TryEvaluateAsync(request, cancellationToken);

        // An admin Include rule can't vouch for Openserve Fibre once the
        // authenticated qualification is available — that is the authority.
        // Exclude rules still apply (SmartFuture doesn't service the area).
        var qualificationAuthority = _qualification?.CanQualify == true;
        if (mapHit.Matched && qualificationAuthority && mapHit.MatchedType == CoverageMapRuleType.Include)
        {
            _logger.LogInformation("[Coverage] Include rule {RuleId} ignored — Openserve Product Qualification decides Fibre eligibility.", mapHit.MatchedRuleId);
        }
        else if (mapHit.Matched)
        {
            var overrideDto = BuildOverrideResponse(mapHit, request);
            // For manual Include rules the whole point of the override
            // is that the admin promises the location IS covered — the
            // customer must be able to pick a package immediately. There
            // is no Openserve line-speed to filter by, so we surface
            // every active public Fibre package. Exclude rules populate
            // nothing (there's nothing to order).
            if (mapHit.MatchedType == CoverageMapRuleType.Include)
            {
                try
                {
                    overrideDto.AvailablePackages = await ListActivePackagesAsync(
                        ServicePackageType.Fibre, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Coverage] Manual-include package list threw for rule {RuleId}; returning empty list.",
                        mapHit.MatchedRuleId);
                    overrideDto.AvailablePackages = new List<ServicePackageCoverageDto>();
                }
            }
            _logger.LogInformation(
                "[Coverage] Bypassing Openserve — matched rule {RuleId} ({RuleName}), source={Source}, packages={Packages}",
                mapHit.MatchedRuleId, mapHit.MatchedRuleName, overrideDto.MatchSource,
                overrideDto.AvailablePackages.Count);
            return Result<CoverageCheckResponseDto>.Success(overrideDto);
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
            if (lat < SaLatMin || lat > SaLatMax || lon < SaLonMin || lon > SaLonMax)
            {
                return Result<CoverageCheckResponseDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Coordinates appear to be outside South Africa. SmartFuture only services South African addresses.");
            }
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

        if (qualificationAuthority)
        {
            var qualified = await CheckWithQualificationAsync(request, lat, lon, formattedAddress, cancellationToken);
            if (qualified is not null) return qualified;
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

        // Phase 47 — server-side package matching. Only when Openserve
        // reports the line as Working AND we have at least an
        // approximate line speed. Failures inside the matcher are
        // swallowed and logged — the coverage result itself is the
        // primary contract, package matching is enrichment.
        if (dto.CoverageAvailable)
        {
            try
            {
                dto.AvailablePackages = await MatchPackagesAsync(dto, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Coverage] Package matching threw for LAT={Lat} LON={Lon}; returning empty list.", lat, lon);
                dto.AvailablePackages = new List<ServicePackageCoverageDto>();
            }
        }

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

    // Authenticated Product Qualification as the Fibre authority. Returns null
    // only if the integration stopped being able to qualify mid-request (the
    // caller then falls back to the legacy lookup). Never exposes AMIDs, raw
    // responses or anything credential-related — only customer-safe facts.
    private async Task<Result<CoverageCheckResponseDto>?> CheckWithQualificationAsync(CoverageCheckRequestDto request, decimal lat, decimal lon, string? formattedAddress,
        CancellationToken cancellationToken)
    {
        // Openserve candidates are matched against the customer's street NUMBER
        // and street, so use the first line the customer gave us that carries a
        // house number ("2 Palmas Street") — a Google route-only component
        // ("Palmas Street") would make every match impossible. Nothing is invented.
        var lines = new[] { request.AddressLine1, FirstSegment(request.AddressText), FirstSegment(request.FormattedAddress), FirstSegment(formattedAddress) }
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).ToList();
        var addressLine1 = lines.FirstOrDefault(l => OpenserveAddressMatcher.SplitStreet(l).Number.Length > 0) ?? lines.FirstOrDefault();
        var query = new OpenserveLocationQuery(lat, lon, addressLine1, request.Suburb, request.City ?? request.Town, request.Province);
        var result = await _qualification!.EvaluateLocationAsync(query, cancellationToken);

        switch (result.Status)
        {
            case OpenserveLocationStatus.NotAuthoritative:
                return null;
            case OpenserveLocationStatus.Evaluated:
                break;
            default:
                return Result<CoverageCheckResponseDto>.Failure(ErrorCodes.UPSTREAM_UNAVAILABLE, $"{result.CustomerTitle} {result.CustomerMessage}".Trim());
        }

        var evidence = result.Evidence!;
        var location = result.Location!;
        // Premises not established (no matching Openserve record, none at all, or
        // a legacy unconfirmed AMID): Fibre at the customer's address is unknown.
        var addressReview = !location.PremisesEstablished;
        var status = location.Status();
        var fibreAvailable = location.State == OpenserveQualificationState.Evaluated && location.AddressCleared && location.Fibre == Shared.Enums.Openserve.OpenserveFibreAvailability.Available;
        var eligible = result.Packages.Where(p => p.Assessment.IsEligible).Select(p => p.ServicePackageId).ToHashSet();

        var packages = new List<ServicePackageCoverageDto>();
        if (fibreAvailable && eligible.Count > 0)
        {
            try
            {
                packages = (await ListActivePackagesAsync(ServicePackageType.Fibre, cancellationToken)).Where(p => eligible.Contains(p.Id)).ToList();
                foreach (var package in packages) package.MatchReason = "Confirmed available at your address";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Coverage] Eligible package lookup threw; returning empty list.");
            }
        }

        var dto = new CoverageCheckResponseDto
        {
            CoverageAvailable = fibreAvailable,
            StatusLabel = fibreAvailable ? "Available" : addressReview ? "Address verification required" : "Unavailable",
            RawStatus = evidence.FtthStatusSummary,
            MaxSpeed = evidence.FibreMaxSpeedMbps,
            MaxSpeedUnit = evidence.FibreMaxSpeedMbps is null ? null : "Mbps",
            MatchedAddress = evidence.CanonicalAddress ?? formattedAddress ?? request.AddressText?.Trim(),
            Suburb = evidence.Suburb,
            Town = evidence.Town,
            Province = evidence.Province,
            DistanceMeters = evidence.DistanceMeters,
            Latitude = lat,
            Longitude = lon,
            Products = fibreAvailable
                ? evidence.Products.Where(p => p.IsImmediatelyAvailable && !string.IsNullOrWhiteSpace(p.ProductCode))
                    .Select(p => new CoverageProductDto { ProductName = p.ProductName, ProductCode = p.ProductCode, UpstreamSpeed = p.UpstreamSpeed, DownstreamSpeed = p.DownstreamSpeed })
                    .ToList()
                : new List<CoverageProductDto>(),
            AvailablePackages = packages,
            FriendlyTitle = result.CustomerTitle,
            FriendlyMessage = result.CustomerMessage,
            MatchSource = CoverageMatchSource.OpenserveQualification,
            AddressReviewRequired = addressReview,
            QualificationReference = result.EvidenceId,
            FibreQualificationStatus = (fibreAvailable && packages.Count > 0 ? OpenserveFibreQualificationStatus.Orderable
                : fibreAvailable ? OpenserveFibreQualificationStatus.ProductUnavailable
                : status).ToString(),
            AddressVerificationRequired = addressReview,
            NearbyOpenserveAddresses = result.AddressCandidates
                .Select(c => new CoverageNearbyAddressDto { Address = c.Address, DistanceMeters = c.DistanceMeters, MatchesYourAddress = c.Match == OpenserveCandidateMatch.Matched })
                .ToList()
        };

        _logger.LogInformation("Coverage check (Openserve Product Qualification): available={Available} addressReview={AddressReview} fibre={Fibre} eligiblePackages={Eligible} evidence={EvidenceId}",
            dto.CoverageAvailable, addressReview, location.Fibre, packages.Count, result.EvidenceId);
        return Result<CoverageCheckResponseDto>.Success(dto);
    }

    private static string? FirstSegment(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    // Build the response payload for an admin Coverage-Map short-
    // circuit. Populates the fields callers already render (StatusLabel,
    // FriendlyTitle/Message, MatchedAddress) plus the new MatchSource +
    // MatchedRuleId + MatchedRuleName so admin/debug tooling can trace
    // the outcome. Openserve is NOT consulted for these; Products and
    // AvailablePackages stay empty because we don't have a line-speed
    // reading to filter by.
    private static CoverageCheckResponseDto BuildOverrideResponse(
        CoverageMapEvaluationResult hit, CoverageCheckRequestDto req)
    {
        var isInclude = hit.MatchedType == CoverageMapRuleType.Include;
        return new CoverageCheckResponseDto
        {
            CoverageAvailable = isInclude,
            StatusLabel       = isInclude ? "Available" : "Unavailable",
            RawStatus         = isInclude ? "CoverageMapInclude" : "CoverageMapExclude",
            MatchedAddress    = req.FormattedAddress
                                 ?? req.AddressText
                                 ?? req.AddressLine1,
            Suburb            = req.Suburb,
            Town              = req.Town,
            Province          = req.Province,
            Latitude          = req.Latitude,
            Longitude         = req.Longitude,
            FriendlyTitle     = isInclude ? "Coverage is available." : "Coverage is not available in this area.",
            FriendlyMessage   = isInclude
                ? "Great news — we can service this area. Continue to pick a package."
                : "We don't cover this area yet. Leave your details and we'll be in touch when service is available.",
            MatchSource       = isInclude ? CoverageMatchSource.CoverageMapInclude : CoverageMatchSource.CoverageMapExclude,
            MatchedRuleId     = hit.MatchedRuleId,
            MatchedRuleName   = hit.MatchedRuleName,
        };
    }

    // List every active public package of the requested service type,
    // WITHOUT a line-speed filter. Used by the manual Coverage-Map
    // include-rule branch — Openserve is bypassed on that path so we
    // have no line speed to gate by, and the admin has explicitly
    // vouched for the location. Ordering matches MatchPackagesAsync
    // (DisplayOrder, then Price) so the UI reads identically whether
    // the coverage answer came from Openserve or an admin rule.
    private async Task<List<ServicePackageCoverageDto>> ListActivePackagesAsync(
        ServicePackageType type, CancellationToken cancellationToken)
    {
        var search = await _servicePackages.SearchCustomerAsync(
            new ServicePackageFilterRequestDto
            {
                Type         = type,
                StatusFilter = ServicePackageStatus.Active,
                Page         = 1,
                PageSize     = 100
            },
            cancellationToken);

        if (!search.IsSuccess || search.Data?.Items is null)
        {
            _logger.LogWarning(
                "[Coverage] Active {Type} package lookup returned {Code} {Message} on manual-include path; returning empty list.",
                type, search.Code, search.Message);
            return new List<ServicePackageCoverageDto>();
        }

        return search.Data.Items
            .Where(p => p.Type == type && p.Status == ServicePackageStatus.Active)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Price)
            .Select(p => new ServicePackageCoverageDto
            {
                Id                  = p.Id,
                Name                = p.Name,
                ShortDescription    = p.ShortDescription,
                SpeedLabel          = p.SpeedLabel,
                DownloadSpeedMbps   = p.DownloadSpeedMbps,
                UploadSpeedMbps     = p.UploadSpeedMbps,
                Price               = p.Price,
                BillingCycle        = p.BillingCycle,
                HasFreeInstallation = p.HasFreeInstallation,
                InstallationFee     = p.InstallationFee,
                IncludesRouter      = p.IncludesRouter,
                RouterDescription   = p.RouterDescription,
                TermsSummary        = p.TermsSummary,
                ExternalReference   = p.ExternalReference,
                IsFeatured          = p.IsFeatured,
                DisplayOrder        = p.DisplayOrder,
                // Distinct from the Openserve "Within X Mbps" reason —
                // an admin override doesn't have a line-speed context;
                // the label just tells the visitor why the package is
                // eligible.
                MatchReason         = "Available in this area"
            })
            .ToList();
    }

    // Phase 47 — match SmartFuture Fibre packages to the line capability
    // Openserve reports. Rules (kept simple on purpose — easy for ops
    // to reason about):
    //
    //   1. Compute the line's effective Mbps as max(fibreMaxSpeed,
    //      parsed-from-product downstream speeds). Openserve sometimes
    //      reports a conservative `fibreMaxSpeed` even when the
    //      product list advertises a higher tier — taking the max
    //      lets the visitor see the full range.
    //   2. Pull active Fibre packages from IServicePackageService.
    //   3. Include every package whose DownloadSpeedMbps <= line max.
    //      Packages with a null DownloadSpeedMbps are EXCLUDED here —
    //      we can't safely judge if they fit, and admins should set a
    //      speed before exposing them.
    //   4. Sort by DisplayOrder, then Price.
    //
    // Returns an empty list when no packages match — the website
    // surfaces a distinct "available but no SmartFuture package
    // matched" message in that case.
    private async Task<List<ServicePackageCoverageDto>> MatchPackagesAsync(CoverageCheckResponseDto coverage, CancellationToken cancellationToken)
    {
        var lineMbps = coverage.MaxSpeed ?? 0m;
        foreach (var product in coverage.Products)
        {
            var down = ParseMbps(product.DownstreamSpeed);
            if (down.HasValue && down.Value > lineMbps) lineMbps = down.Value;
        }

        if (lineMbps <= 0m)
        {
            // Coverage available but no usable speed reading — be
            // conservative and return nothing so the UI prompts the
            // visitor to confirm the package list with us manually.
            return new List<ServicePackageCoverageDto>();
        }

        var search = await _servicePackages.SearchCustomerAsync(
            new ServicePackageFilterRequestDto
            {
                Type         = ServicePackageType.Fibre,
                StatusFilter = ServicePackageStatus.Active,
                Page         = 1,
                PageSize     = 100
            },
            cancellationToken);

        if (!search.IsSuccess || search.Data?.Items is null)
        {
            _logger.LogWarning(
                "[Coverage] Active Fibre package lookup returned {Code} {Message}; returning empty match list.",
                search.Code, search.Message);
            return new List<ServicePackageCoverageDto>();
        }

        var matched = search.Data.Items
            .Where(p => p.Type   == ServicePackageType.Fibre
                     && p.Status == ServicePackageStatus.Active
                     && p.DownloadSpeedMbps.HasValue
                     && (decimal)p.DownloadSpeedMbps.Value <= lineMbps)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Price)
            .Select(p => new ServicePackageCoverageDto
            {
                Id                  = p.Id,
                Name                = p.Name,
                ShortDescription    = p.ShortDescription,
                SpeedLabel          = p.SpeedLabel,
                DownloadSpeedMbps   = p.DownloadSpeedMbps,
                UploadSpeedMbps     = p.UploadSpeedMbps,
                Price               = p.Price,
                BillingCycle        = p.BillingCycle,
                HasFreeInstallation = p.HasFreeInstallation,
                InstallationFee     = p.InstallationFee,
                IncludesRouter      = p.IncludesRouter,
                RouterDescription   = p.RouterDescription,
                TermsSummary        = p.TermsSummary,
                ExternalReference   = p.ExternalReference,
                IsFeatured          = p.IsFeatured,
                DisplayOrder        = p.DisplayOrder,
                MatchReason         = $"Within {lineMbps:0.#} Mbps line capability"
            })
            .ToList();

        if (_environment.IsDevelopment())
        {
            _logger.LogInformation(
                "[Coverage] Matched {MatchedCount} of {TotalCount} active Fibre package(s) at <= {Line} Mbps.",
                matched.Count, search.Data.Items.Count, lineMbps);
        }

        return matched;
    }
}

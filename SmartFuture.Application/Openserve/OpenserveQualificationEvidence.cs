using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Turns one Product Qualification call into an <see cref="OpenserveQualificationResult"/>
/// evidence row (facts in columns, products as rows), and evaluates it for a
/// specific customer address and package mapping.
/// </summary>
public static class OpenserveQualificationEvidence
{
    public static OpenserveQualificationResult Build(OpenserveApiCallResult<OpenserveQualificationOutcome> call, OpenserveQualificationPurpose purpose, decimal? queryLatitude, decimal? queryLongitude,
        string? queryAmid, Guid? integrationLogId, DateTime nowUtc)
    {
        var facts = call.Outcome?.Facts ?? (call.Outcome is { } outcome ? FromOutcome(outcome) : null);
        var evidence = new OpenserveQualificationResult
        {
            Purpose = purpose,
            IntegrationLogId = integrationLogId,
            QualifiedAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            QueryLatitude = queryLatitude,
            QueryLongitude = queryLongitude,
            QueryAmid = Clip(queryAmid, 60),
            CallSucceeded = call.IsSuccess && facts is not null,
            HttpStatusCode = call.HttpStatusCode,
            ErrorCode = call.IsSuccess ? null : Clip(call.ErrorCode, 120),
            ErrorMessage = call.IsSuccess ? null : Clip(call.ErrorMessage, 1000)
        };
        if (!evidence.CallSucceeded) return evidence;

        if (facts!.Address is { } a)
        {
            evidence.Amid = Clip(a.Amid, 60);
            evidence.AddressIdentified = !string.IsNullOrWhiteSpace(a.Amid);
            evidence.CanonicalAddress = Clip(a.FullAddress, 400);
            evidence.StreetNumber = Clip(a.StreetNumber, 40);
            evidence.StreetName = Clip(a.StreetName, 200);
            evidence.StreetType = Clip(a.StreetType, 40);
            evidence.Suburb = Clip(a.Suburb, 150);
            evidence.Town = Clip(a.Town, 150);
            evidence.Province = Clip(a.Province, 150);
            evidence.Region = Clip(a.Region, 150);
            evidence.Country = Clip(a.Country, 100);
            evidence.Latitude = Coordinate(a.Latitude, 90m);
            evidence.Longitude = Coordinate(a.Longitude, 180m);
            evidence.AddressStatus = Clip(a.Status, 100);
            evidence.DistanceMeters = a.DistanceMeters is { } m && m < 10_000_000_000m ? Math.Round(m, 2) : null;
            evidence.DistanceText = Clip(a.DistanceText, 60);
            evidence.MduVerification = Clip(a.MduVerification, 500);
            evidence.AddressMessage = Clip(a.AddressMessage, 500);
        }

        evidence.BuildingCandidateCount = Math.Max(facts.Buildings.Count, call.Outcome?.BuildingMatchCount ?? 0);
        evidence.BuildingCandidatesJson = OpenserveBuildingCandidates.Serialize(facts.Buildings);

        foreach (var infrastructure in facts.Ftth)
        {
            var maxMbps = infrastructure.MaxSpeedMbps;
            var products = infrastructure.Products.Count == 0 ? new OpenserveQualificationProduct?[] { null } : infrastructure.Products.Cast<OpenserveQualificationProduct?>().ToArray();
            foreach (var product in products)
            {
                evidence.Products.Add(new OpenserveQualifiedProduct
                {
                    QualificationResultId = evidence.Id,
                    InfrastructureIndex = infrastructure.Index,
                    InfrastructureType = Clip(infrastructure.Type, 60),
                    FtthStatus = Clip(infrastructure.Status, 60),
                    ServiceProviderId = Clip(infrastructure.ServiceProviderId, 60),
                    IsImmediatelyAvailable = infrastructure.IsImmediatelyAvailable,
                    FibreMaxSpeedMbps = maxMbps,
                    ProductCode = Clip(product?.ProductCode, 20),
                    ProductName = Clip(product?.ProductName, 150),
                    UpstreamSpeed = Clip(product?.UpstreamSpeed, 40),
                    DownstreamSpeed = Clip(product?.DownstreamSpeed, 40),
                    UpstreamMbps = OpenserveQualificationParser.ParseMbps(product?.UpstreamSpeed),
                    DownstreamMbps = OpenserveQualificationParser.ParseMbps(product?.DownstreamSpeed),
                    CreatedAtUtc = nowUtc
                });
            }
        }

        evidence.FtthInfrastructureCount = facts.Ftth.Count;
        evidence.FibreAvailability = OpenserveFibreEligibility.EvaluateFibre(evidence.Products.ToList());
        evidence.FtthStatusSummary = Clip(string.Join("; ", facts.Ftth.Select(f => string.IsNullOrWhiteSpace(f.Type) ? f.Status ?? "no status" : $"{f.Status ?? "no status"} ({f.Type})")), 300);
        evidence.FibreMaxSpeedMbps = facts.Ftth.Where(f => f.IsImmediatelyAvailable).Select(f => f.MaxSpeedMbps).Max();
        evidence.AvailableProductCodes = Clip(string.Join(",", evidence.Products.Where(p => p.IsImmediatelyAvailable && p.ProductCode is not null)
            .Select(p => p.ProductCode!.ToUpperInvariant()).Distinct()), 500);
        evidence.EthernetProductCodes = Clip(string.Join(",", facts.EthernetProductCodes), 300);
        evidence.FwaStatus = Clip(facts.FwaStatus, 100);
        return evidence;
    }

    /// <summary>
    /// Facts for an outcome that didn't carry the full parse (built by an older
    /// path): re-read from the outcome's own fields only — its single FTTH
    /// status/products, its buildings. Nothing is invented; no street fields
    /// means the address can't be confirmed (review required).
    /// </summary>
    private static OpenserveQualificationFacts FromOutcome(OpenserveQualificationOutcome o)
    {
        var products = o.AvailableProducts ?? Array.Empty<OpenserveQualificationProduct>();
        var ftth = o.FtthStatus is null && products.Count == 0
            ? Array.Empty<OpenserveFtthInfrastructure>()
            : new[] { new OpenserveFtthInfrastructure(0, o.FtthStatus, null, null, o.FibreMaxSpeed, o.FibreMaxSpeedUnit, products) };
        IReadOnlyList<OpenserveQualificationBuilding> buildings = o.Buildings is { Count: > 0 } rows ? rows
            : o.BuildingNumId is not null ? new[] { new OpenserveQualificationBuilding(o.Amid, o.BuildingNumId, null, null, null, null, null) }
            : Array.Empty<OpenserveQualificationBuilding>();
        return new OpenserveQualificationFacts(0, null, null,
            new OpenserveQualificationAddress(o.Amid, o.MatchedAddress, null, null, null, o.Suburb, o.Town, o.Province, null, null, null, null, null, null, null, null, null),
            ftth, buildings, Array.Empty<string>(), null);
    }

    /// <summary>A fresh row carrying the same Openserve facts (for reuse of a recent call at the same coordinates) — evaluation and Admin acceptance are NOT copied.</summary>
    public static OpenserveQualificationResult CopyFacts(OpenserveQualificationResult source, OpenserveQualificationPurpose purpose, DateTime nowUtc)
    {
        var copy = new OpenserveQualificationResult
        {
            Purpose = purpose,
            IntegrationLogId = source.IntegrationLogId,
            QualifiedAtUtc = source.QualifiedAtUtc,
            CreatedAtUtc = nowUtc,
            QueryLatitude = source.QueryLatitude,
            QueryLongitude = source.QueryLongitude,
            QueryAmid = source.QueryAmid,
            CallSucceeded = source.CallSucceeded,
            HttpStatusCode = source.HttpStatusCode,
            ErrorCode = source.ErrorCode,
            ErrorMessage = source.ErrorMessage,
            AddressIdentified = source.AddressIdentified,
            Amid = source.Amid,
            CanonicalAddress = source.CanonicalAddress,
            StreetNumber = source.StreetNumber,
            StreetName = source.StreetName,
            StreetType = source.StreetType,
            Suburb = source.Suburb,
            Town = source.Town,
            Province = source.Province,
            Region = source.Region,
            Country = source.Country,
            Latitude = source.Latitude,
            Longitude = source.Longitude,
            AddressStatus = source.AddressStatus,
            DistanceMeters = source.DistanceMeters,
            DistanceText = source.DistanceText,
            MduVerification = source.MduVerification,
            AddressMessage = source.AddressMessage,
            BuildingCandidateCount = source.BuildingCandidateCount,
            BuildingCandidatesJson = source.BuildingCandidatesJson,
            FibreAvailability = source.FibreAvailability,
            FtthStatusSummary = source.FtthStatusSummary,
            FtthInfrastructureCount = source.FtthInfrastructureCount,
            FibreMaxSpeedMbps = source.FibreMaxSpeedMbps,
            AvailableProductCodes = source.AvailableProductCodes,
            EthernetProductCodes = source.EthernetProductCodes,
            FwaStatus = source.FwaStatus
        };
        foreach (var p in source.Products)
        {
            copy.Products.Add(new OpenserveQualifiedProduct
            {
                QualificationResultId = copy.Id,
                InfrastructureIndex = p.InfrastructureIndex,
                InfrastructureType = p.InfrastructureType,
                FtthStatus = p.FtthStatus,
                ServiceProviderId = p.ServiceProviderId,
                IsImmediatelyAvailable = p.IsImmediatelyAvailable,
                FibreMaxSpeedMbps = p.FibreMaxSpeedMbps,
                ProductCode = p.ProductCode,
                ProductName = p.ProductName,
                UpstreamSpeed = p.UpstreamSpeed,
                DownstreamSpeed = p.DownstreamSpeed,
                UpstreamMbps = p.UpstreamMbps,
                DownstreamMbps = p.DownstreamMbps,
                CreatedAtUtc = nowUtc
            });
        }
        return copy;
    }

    /// <summary>
    /// Records, on the evidence row, how it was judged for this customer
    /// address and package (the as-of-run snapshot Admin and reports read).
    /// The gates re-evaluate the product against the current mapping anyway.
    /// </summary>
    public static OpenserveEligibilityAssessment Evaluate(OpenserveQualificationResult evidence, OpenserveAddressMatcher.CustomerAddress customer, Guid? servicePackageId,
        PackageOpenserveMapping? mapping, int? packageDownloadMbps)
    {
        evidence.CustomerAddress = Clip(customer.Display, 600);
        if (evidence.CallSucceeded && evidence.AddressIdentified)
        {
            var (match, detail) = OpenserveAddressMatcher.Compare(customer, evidence);
            evidence.AddressMatch = match;
            evidence.AddressMatchDetail = Clip(detail, 1000);
        }
        else
        {
            evidence.AddressMatch = OpenserveAddressMatch.NotEvaluated;
            evidence.AddressMatchDetail = null;
        }

        var assessment = OpenserveFibreEligibility.Assess(evidence, mapping, packageDownloadMbps);
        if (servicePackageId is null)
        {
            // Location-only check (coverage check): no package to judge yet.
            evidence.ProductEligibility = OpenserveProductEligibility.NotEvaluated;
            return assessment;
        }

        evidence.ServicePackageId = servicePackageId;
        evidence.MappingSku = Clip(mapping?.Sku, 20);
        evidence.MappingCapacity = Clip(mapping?.Capacity, 20);
        evidence.MappingCapacityUom = Clip(mapping?.CapacityUom, 20);
        evidence.ProductEligibility = assessment.Product;
        evidence.EligibilityReason = Clip(assessment.ProductReason, 1000);
        return assessment;
    }

    public static OpenserveAddressMatcher.CustomerAddress CustomerAddressOf(Order order) => new(order.AddressLine1, order.Suburb, order.City, order.Province);

    public static OpenserveQualificationPurpose PurposeOf(OpenserveQualificationTrigger trigger) => trigger switch
    {
        OpenserveQualificationTrigger.PaymentConversion => OpenserveQualificationPurpose.PaymentConversion,
        OpenserveQualificationTrigger.SubmissionSelfHeal => OpenserveQualificationPurpose.SubmissionSelfHeal,
        OpenserveQualificationTrigger.AdminManual => OpenserveQualificationPurpose.AdminManual,
        OpenserveQualificationTrigger.BuildingCandidatesRefresh => OpenserveQualificationPurpose.BuildingCandidatesRefresh,
        _ => OpenserveQualificationPurpose.OrderCreated
    };

    /// <summary>Same point to the precision stored (6 dp ≈ 0.1 m).</summary>
    public static bool SameCoordinates(decimal? lat1, decimal? lon1, decimal? lat2, decimal? lon2) =>
        lat1 is { } a && lon1 is { } b && lat2 is { } c && lon2 is { } d && Math.Round(a, 6) == Math.Round(c, 6) && Math.Round(b, 6) == Math.Round(d, 6);

    public static decimal? RoundCoordinate(decimal? value) => value is { } v ? Math.Round(v, 6, MidpointRounding.AwayFromZero) : null;

    private static decimal? Coordinate(decimal? value, decimal limit) => value is { } v && v >= -limit && v <= limit ? Math.Round(v, 6, MidpointRounding.AwayFromZero) : null;

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

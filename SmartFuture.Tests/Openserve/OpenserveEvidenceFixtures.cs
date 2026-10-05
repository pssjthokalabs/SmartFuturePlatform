using System.Globalization;
using System.Text.Json;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Tests.Openserve;

/// <summary>
/// Product Qualification responses as the client now returns them (fully
/// parsed Facts), and qualification evidence for orders — an AMID alone no
/// longer makes an order submittable, so tests that expect a submission
/// seed evidence that says Fibre and the mapped product are available.
/// </summary>
internal static class OpenserveEvidenceFixtures
{
    /// <summary>Every Fibre SKU the tests map, offered on Openserve's own network at 1000/500.</summary>
    internal static readonly string[] DefaultSkus = { "OFC", "OFCP", "OWC", "OWS", "OOCF", "OFV", "OIB", "OWCP", "OWSP" };

    internal static OpenserveFtthInfrastructure OwnNetwork(string status = "Working", string down = "1000 Mbps", string up = "500 Mbps", decimal? maxSpeed = 1000m, params string[] skus) =>
        new(0, status, null, "0", maxSpeed, "Mbps",
            (skus.Length == 0 ? DefaultSkus : skus).Select(s => new OpenserveQualificationProduct(OpenserveProductCatalogue.ProductNameFor(s) ?? s, s, up, down)).ToList());

    internal static OpenserveFtthInfrastructure ThirdParty(string status = "pending", params string[] skus) =>
        new(1, status, "3rd_Party", "M_21", 500m, "Mbps",
            (skus.Length == 0 ? new[] { "OFCTP", "OFCPTP", "OWCTP" } : skus).Select(s => new OpenserveQualificationProduct(s, s, "250 Mbps", "500 Mbps")).ToList());

    /// <summary>A Product Qualification answer. Defaults: "61 OAK AVE HIGHVELD CENTURION", Fibre Working with <see cref="DefaultSkus"/>.</summary>
    internal static OpenserveQualificationFacts Facts(string? amid, string? streetNo = "61", string? street = "OAK", string? streetType = "AVE", string? suburb = "HIGHVELD",
        string? town = "CENTURION", IReadOnlyList<OpenserveFtthInfrastructure>? ftth = null, IReadOnlyList<OpenserveQualificationBuilding>? buildings = null, decimal? distance = 0m,
        decimal latitude = -26.095950m, decimal longitude = 27.927632m)
    {
        var full = string.Join(" ", new[] { streetNo, street, streetType, suburb, town }.Where(p => !string.IsNullOrWhiteSpace(p)));
        return new OpenserveQualificationFacts(0, "OK", string.Empty,
            new OpenserveQualificationAddress(amid, full, streetNo, street, streetType, suburb, town, "GAUTENG", "GAUTENG CENTRAL", "SOUTH AFRICA", latitude, longitude, "Verified",
                distance, distance is null ? null : $"{distance:0.00} m", "Not applicable.", null),
            ftth ?? new[] { OwnNetwork() }, buildings ?? new List<OpenserveQualificationBuilding>(), new[] { "OUC" }, "Unavailable");
    }

    internal static OpenserveApiCallResult<OpenserveQualificationOutcome> Call(OpenserveQualificationFacts facts)
    {
        var primary = facts.Ftth.FirstOrDefault(f => f.IsImmediatelyAvailable) ?? facts.Ftth.FirstOrDefault();
        return OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(Guid.NewGuid().ToString(), "GET", "https://stapitrx.openserve.co.za/ws-marut/productqualification", 200, string.Empty,
            "{\"errorCode\":0}",
            new OpenserveQualificationOutcome(facts.Address?.Amid, facts.Buildings.Count == 1 ? facts.Buildings[0].BldNumId : null, facts.Buildings.Count, facts.Address?.FullAddress,
                primary?.Status, primary?.MaxSpeed, primary?.MaxSpeedUnit, facts.Address?.Suburb, facts.Address?.Town, facts.Address?.Province,
                facts.Ftth.SelectMany(f => f.Products).ToList(), facts.Buildings, facts),
            "{\"MessageID\":\"m\",\"api_key\":\"***REDACTED***\"}");
    }

    /// <summary>Wraps a raw Openserve body exactly as OpenserveApiClient does (real parser, real outcome).</summary>
    internal static OpenserveApiCallResult<OpenserveQualificationOutcome> FromJson(string json)
    {
        const string headers = "{\"MessageID\":\"m\",\"api_key\":\"***REDACTED***\"}";
        const string endpoint = "https://stapitrx.openserve.co.za/ws-marut/productqualification";
        var facts = OpenserveQualificationParser.Parse(json);
        var primary = facts.Ftth.FirstOrDefault(f => f.IsImmediatelyAvailable) ?? facts.Ftth.FirstOrDefault();
        var outcome = new OpenserveQualificationOutcome(facts.Address?.Amid, facts.Buildings.Count == 1 ? facts.Buildings[0].BldNumId : null, facts.Buildings.Count, facts.Address?.FullAddress,
            primary?.Status, primary?.MaxSpeed, primary?.MaxSpeedUnit, facts.Address?.Suburb, facts.Address?.Town, facts.Address?.Province, facts.Ftth.SelectMany(f => f.Products).ToList(),
            facts.Buildings, facts);
        return facts.IsOk
            ? OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(Guid.NewGuid().ToString(), "GET", endpoint, 200, string.Empty, json, outcome, headers)
            : OpenserveApiCallResult<OpenserveQualificationOutcome>.Failure(Guid.NewGuid().ToString(), "GET", endpoint, 200, string.Empty, json, facts.ErrorCode?.ToString(), facts.Message ?? "error",
                headers);
    }

    /// <summary>A FORCEVERIFY=Y answer (root "address"/"LAT"/"LON" + AddressVerify[]), in the exact shape staging returned.</summary>
    internal static OpenserveApiCallResult<OpenserveQualificationOutcome> Verify(params (string Amid, string Address, decimal Dist, decimal Lat, decimal Lon)[] candidates)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["address"] = "",
            ["LAT"] = candidates.Length > 0 ? candidates[0].Lat.ToString(CultureInfo.InvariantCulture) : "",
            ["LON"] = candidates.Length > 0 ? candidates[0].Lon.ToString(CultureInfo.InvariantCulture) : "",
            ["AddressVerify"] = candidates.Select(c => new Dictionary<string, object>
            {
                ["AMID"] = c.Amid, ["DIST"] = c.Dist, ["LR_Address"] = c.Address, ["LR_LAT"] = c.Lat.ToString(CultureInfo.InvariantCulture),
                ["LR_LON"] = c.Lon.ToString(CultureInfo.InvariantCulture), ["DIST_M"] = $"{c.Dist.ToString("0.00", CultureInfo.InvariantCulture)} m"
            }).ToList()
        });
        return FromJson(json);
    }

    /// <summary>
    /// The FORCEVERIFY answer a scripted qualification implies: its own address
    /// record as the only candidate (DIST 0). No AMID → no candidates.
    /// </summary>
    internal static OpenserveApiCallResult<OpenserveQualificationOutcome> VerifyFrom(OpenserveApiCallResult<OpenserveQualificationOutcome> qualification)
    {
        var amid = qualification.Outcome?.Facts?.Address?.Amid ?? qualification.Outcome?.Amid;
        if (string.IsNullOrWhiteSpace(amid)) return Verify();
        var address = qualification.Outcome?.Facts?.Address?.FullAddress ?? qualification.Outcome?.MatchedAddress ?? string.Empty;
        return Verify((amid, address, 0m, qualification.Outcome?.Facts?.Address?.Latitude ?? -26.095950m, qualification.Outcome?.Facts?.Address?.Longitude ?? 27.927632m));
    }

    /// <summary>
    /// Links eligible evidence to the order (Fibre Working, every default SKU at
    /// 1000/500, address matched) — what a successful qualification of the
    /// order's own address records. The order must already have its AMID.
    /// </summary>
    internal static async Task<OpenserveQualificationResult> SeedEligibleEvidenceAsync(IAppDbContext db, Order order, OpenserveQualificationFacts? facts = null,
        OpenserveAddressMatch addressMatch = OpenserveAddressMatch.Matched)
    {
        var evidence = OpenserveQualificationEvidence.Build(Call(facts ?? Facts(order.OpenserveAmId)), OpenserveQualificationPurpose.OrderCreated,
            OpenserveQualificationEvidence.RoundCoordinate(order.Latitude), OpenserveQualificationEvidence.RoundCoordinate(order.Longitude), null, null, DateTime.UtcNow);
        evidence.OrderId = order.Id;
        evidence.CustomerAddress = order.AddressLine1;
        evidence.AddressMatch = addressMatch;
        evidence.AddressMatchDetail = addressMatch == OpenserveAddressMatch.Matched ? "Street number and street match." : "Street number differs.";
        db.OpenserveQualificationResults.Add(evidence);
        order.OpenserveQualificationResultId = evidence.Id;
        order.OpenserveQualifiedAtUtc ??= evidence.QualifiedAtUtc;
        await db.SaveChangesAsync();
        return evidence;
    }

    /// <summary>The exact FORCEVERIFY=Y body staging returned for the 2 Palmas Street coordinates (-25.866217, 28.106903) — none is number 2.</summary>
    internal const string UatAddressVerifyResponse = """
        {
          "address": "",
          "LAT": "-25.866217",
          "LON": "28.106903",
          "AddressVerify": [
            { "AMID": "52782141", "DIST": 0.0, "LR_Address": "8 PALMAS ST MONAVONI X 6 CENTURION", "LR_LAT": "-25.866217", "LR_LON": "28.106903", "DIST_M": ".00 m" },
            { "AMID": "80573005", "DIST": 26.7342855017672, "LR_Address": "11A DE OVALLE BLV MONAVONI X 6 CENTURION", "LR_LAT": "-25.866414", "LR_LON": "28.107057", "DIST_M": "26.73 m" },
            { "AMID": "52782142", "DIST": 26.7342855017672, "LR_Address": "11 DE OVALLE BLV MONAVONI X 6 CENTURION", "LR_LAT": "-25.866414", "LR_LON": "28.107057", "DIST_M": "26.73 m" }
          ]
        }
        """;

    /// <summary>The exact Product Qualification body Openserve returned in UAT for SF-20261004-66E5A8CE — an AMID, no ftthInfrastructure.</summary>
    internal const string UatNoFtthResponse = """
        {
          "errorCode": 0,
          "errorString": "OK",
          "message": "",
          "Results": {
            "payload": {
              "AddressInfo": {
                "AMID": "52782141",
                "LR_Address": "8 PALMAS ST MONAVONI X 6 CENTURION",
                "LR_LAT": "-25.866217",
                "LR_LON": "28.106903",
                "LR_SUBURB": "MONAVONI X 6",
                "LR_STREET": "PALMAS",
                "LR_STREET_TYPE": "ST",
                "LR_STREET_NO": "8",
                "LR_TOWN": "CENTURION",
                "LR_PROVINCE": "GAUTENG",
                "LR_STATUS": "Verified",
                "REGION": "NORTH EASTERN",
                "LR_COUNTRY": "SOUTH AFRICA",
                "DIST_M": "32.77 m",
                "MDU_Verification": "Not applicable."
              },
              "ethernetInfrastructure": {
                "ethernetInfo": [
                  {
                    "Ethernet_Type": "OUC Access",
                    "District": "GT_Cluster",
                    "Zone": "2",
                    "EthernetProductInfo": [
                      { "ProductName": "Openserve Uni Connect", "ProductCode": "OUC" },
                      { "ProductName": "Enterprise One Cloud", "ProductCode": "EOC" }
                    ]
                  }
                ]
              },
              "fwaInfo": { "fwa_Status": "Unavailable", "fwaMaxSpeed": 0 },
              "otnInfrastructure": { "otnInfo": [ { "OTN_Zone": "0" } ] }
            }
          }
        }
        """;
}

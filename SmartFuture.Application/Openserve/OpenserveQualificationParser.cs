using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Reads a Product Qualification response (spec §3) completely and
/// tolerantly. The spec is inconsistent about names, so every documented
/// spelling is accepted:
///   • FTTH under <c>ftthInfrastructure.ftthInfo[]</c> (§3.2 sample) or
///     <c>ftthOSInfo</c> (§3 field table), as an array or a single object,
///     at payload level or inside ftthInfrastructure;
///   • every FTTH entry, not just the first — the sample has Openserve's own
///     network AND a "3rd_Party" entry with different (…TP) products;
///   • products under <c>ftthProductInfo</c> (array or object);
///   • numbers sent as numbers or strings; property names in any case.
///   • a FORCEVERIFY=Y coordinate lookup, which answers with nearby Address
///     Master candidates instead of a qualification (confirmed against
///     staging: root-level "address", "LAT", "LON" and "AddressVerify":
///     [{ AMID, DIST, LR_Address, LR_LAT, LR_LON, DIST_M }]).
/// An AMID only identifies the address. Whether Fibre is available is
/// decided from the FTTH entries, never from the AMID.
/// </summary>
public static class OpenserveQualificationParser
{
    /// <summary>Throws <see cref="JsonException"/> when the body isn't JSON (the client turns that into PARSE_ERROR).</summary>
    public static OpenserveQualificationFacts Parse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return OpenserveQualificationFacts.Empty;

        var payload = Child(Child(root, "Results"), "payload") ?? Child(root, "payload") ?? Child(root, "Results");
        var addressElement = Child(payload, "AddressInfo");

        var buildings = Items(Child(Child(addressElement, "buildingNumberInfo"), "buildingInfo"))
            .Concat(Items(Child(Child(payload, "buildingNumberInfo"), "buildingInfo")))
            .Select(b => new OpenserveQualificationBuilding(Text(b, "AM_ID"), Text(b, "BLD_NUM_ID"), Text(b, "BLD_ID"), Text(b, "FLOOR_ID"), Text(b, "NUM"), Text(b, "BUILDING_NAME"), Text(b, "FLOOR")))
            .ToList();

        var ftthContainer = Child(payload, "ftthInfrastructure");
        var ftthElements = Items(Child(ftthContainer, "ftthInfo"))
            .Concat(Items(Child(ftthContainer, "ftthOSInfo")))
            .Concat(ftthContainer is { ValueKind: JsonValueKind.Array } ? Items(ftthContainer) : Enumerable.Empty<JsonElement>())
            .Concat(Items(Child(payload, "ftthOSInfo")))
            .Concat(Items(Child(payload, "ftthInfo")))
            .ToList();

        var ftth = ftthElements.Select((e, index) =>
        {
            var products = Items(Child(e, "ftthProductInfo"))
                .Select(p => new OpenserveQualificationProduct(Text(p, "ProductName"), Text(p, "ProductCode"), Text(p, "upstreamSpeed"), Text(p, "downstreamSpeed")))
                .ToList();
            return new OpenserveFtthInfrastructure(index, Text(e, "FTTH_Status"), Text(e, "FTTH_Type"), Text(e, "FTTHServiceProviderID"), Number(e, "fibreMaxSpeed"), Text(e, "fibreMaxSpeedUnit"), products);
        }).ToList();

        var ethernetCodes = Items(Child(Child(payload, "ethernetInfrastructure"), "ethernetInfo"))
            .SelectMany(e => Items(Child(e, "EthernetProductInfo")))
            .Select(p => Text(p, "ProductCode"))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        OpenserveQualificationAddress? address = null;
        if (addressElement is { ValueKind: JsonValueKind.Object } a)
        {
            var distanceText = Text(a, "DIST_M");
            address = new OpenserveQualificationAddress(
                Text(a, "AMID"), Text(a, "LR_Address"), Text(a, "LR_STREET_NO"), Text(a, "LR_STREET"), Text(a, "LR_STREET_TYPE"), Text(a, "LR_SUBURB"), Text(a, "LR_TOWN"),
                Text(a, "LR_PROVINCE"), Text(a, "REGION"), Text(a, "LR_COUNTRY"), Number(a, "LR_LAT"), Number(a, "LR_LON"), Text(a, "LR_STATUS"), ParseDistanceMeters(distanceText),
                distanceText, Text(a, "MDU_Verification"), Text(a, "AddrMsg"));
        }

        // FORCEVERIFY=Y: candidates are listed as returned — nothing is
        // chosen here (the closest one is NOT assumed to be the customer's).
        var verifyElement = Child(root, "AddressVerify") ?? Child(payload, "AddressVerify");
        var candidates = Items(verifyElement)
            .Select(c =>
            {
                var distanceText = Text(c, "DIST_M");
                return new OpenserveAddressCandidate(Text(c, "AMID"), Number(c, "DIST") ?? ParseDistanceMeters(distanceText), distanceText, Text(c, "LR_Address"),
                    Number(c, "LR_LAT"), Number(c, "LR_LON"));
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.Amid))
            .ToList();

        return new OpenserveQualificationFacts(
            Integer(root, "errorCode"), Text(root, "errorString"), Text(root, "message"), address, ftth, buildings, ethernetCodes, Text(Child(payload, "fwaInfo"), "fwa_Status"),
            candidates, verifyElement is not null);
    }

    /// <summary>"250 Mbps" → 250, "1 Gbps" → 1000, "512 kbps" → 0.512, "100" → 100. Null when nothing numeric.</summary>
    public static decimal? ParseMbps(string? text, string? unit = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = SpeedPattern.Match(text);
        if (!match.Success || !decimal.TryParse(match.Groups["value"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        var u = (match.Groups["unit"].Success && match.Groups["unit"].Value.Length > 0 ? match.Groups["unit"].Value : unit ?? string.Empty).Trim().ToLowerInvariant();
        return u switch
        {
            "kbps" or "kb/s" => Math.Round(value / 1000m, 3),
            "gbps" or "gb/s" => value * 1000m,
            _ => value
        };
    }

    /// <summary>DIST_M ("32.77 m", ".00 m", "1.2 km") → metres.</summary>
    public static decimal? ParseDistanceMeters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = DistancePattern.Match(text);
        if (!match.Success || !decimal.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        return match.Groups["unit"].Value.Equals("km", StringComparison.OrdinalIgnoreCase) ? value * 1000m : value;
    }

    private static readonly Regex SpeedPattern = new(@"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>kbps|kb/s|mbps|mbit/s|mbit|gbps|gb/s)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DistancePattern = new(@"(?<value>\d*\.?\d+)\s*(?<unit>km|m)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static JsonElement? Child(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p) return null;
        foreach (var property in p.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return property.Value;
        }
        return null;
    }

    /// <summary>An array's object items, or a single object as one item.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList(),
        { ValueKind: JsonValueKind.Object } single => new[] { single },
        _ => Enumerable.Empty<JsonElement>()
    };

    private static string? Text(JsonElement? parent, string name)
    {
        var value = Child(parent, name);
        if (value is not { } v) return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static decimal? Number(JsonElement? parent, string name)
    {
        var value = Child(parent, name);
        if (value is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var number)) return number;
        return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static int? Integer(JsonElement? parent, string name)
    {
        var number = Number(parent, name);
        return number is { } n && n == decimal.Truncate(n) && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
    }
}

/// <summary>Everything a Product Qualification response said, read in full. <see cref="AddressCandidates"/> is a FORCEVERIFY=Y answer's AddressVerify[] (<see cref="AddressVerifyReturned"/> = the list was present, possibly empty).</summary>
public sealed record OpenserveQualificationFacts(
    int? ErrorCode, string? ErrorString, string? Message, OpenserveQualificationAddress? Address, IReadOnlyList<OpenserveFtthInfrastructure> Ftth,
    IReadOnlyList<OpenserveQualificationBuilding> Buildings, IReadOnlyList<string> EthernetProductCodes, string? FwaStatus,
    IReadOnlyList<OpenserveAddressCandidate>? AddressCandidates = null, bool AddressVerifyReturned = false)
{
    public static OpenserveQualificationFacts Empty { get; } =
        new(null, null, null, null, Array.Empty<OpenserveFtthInfrastructure>(), Array.Empty<OpenserveQualificationBuilding>(), Array.Empty<string>(), null);

    public bool IsOk => ErrorCode is null or 0;
}

/// <summary>One AddressVerify[] entry: a nearby Openserve Address Master record. DIST is metres from the queried point.</summary>
public sealed record OpenserveAddressCandidate(string? Amid, decimal? DistanceMeters, string? DistanceText, string? Address, decimal? Latitude, decimal? Longitude);

/// <summary>AddressInfo (§3.1.1.2) — Openserve's canonical (LR_*) address for the AMID.</summary>
public sealed record OpenserveQualificationAddress(
    string? Amid, string? FullAddress, string? StreetNumber, string? StreetName, string? StreetType, string? Suburb, string? Town, string? Province, string? Region,
    string? Country, decimal? Latitude, decimal? Longitude, string? Status, decimal? DistanceMeters, string? DistanceText, string? MduVerification, string? AddressMessage);

/// <summary>One FTTH infrastructure entry (§3.1.1.4) with its products.</summary>
public sealed record OpenserveFtthInfrastructure(
    int Index, string? Status, string? Type, string? ServiceProviderId, decimal? MaxSpeed, string? MaxSpeedUnit, IReadOnlyList<OpenserveQualificationProduct> Products)
{
    /// <summary>§3.1.1.4: "Working" and "Available" are immediately available; "Future (Planned)", "Pre Order" (and the sample's "pending") are not.</summary>
    public bool IsImmediatelyAvailable => OpenserveFtthStatus.IsImmediatelyAvailable(Status);

    public decimal? MaxSpeedMbps => MaxSpeed is { } speed ? OpenserveQualificationParser.ParseMbps(speed.ToString(CultureInfo.InvariantCulture), MaxSpeedUnit) : null;
}

public static class OpenserveFtthStatus
{
    public static bool IsImmediatelyAvailable(string? status) =>
        status is not null && (status.Trim().Equals("Working", StringComparison.OrdinalIgnoreCase) || status.Trim().Equals("Available", StringComparison.OrdinalIgnoreCase));
}

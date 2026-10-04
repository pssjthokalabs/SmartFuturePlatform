using System.Text.Json;
using SmartFuture.Domain.Orders;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Picks the one Product Qualification buildingInfo row that belongs to
/// this customer, so the Create Order place can carry buildingName /
/// floor / unit / buildingNumId "exactly per product qualification API"
/// (Postman UC 1 place remarks — mandatory for a multi dwelling unit).
///
/// Never guesses: a single returned row is used as-is; several rows are
/// narrowed by the customer's own UnitNumber (matched against NUM) and,
/// if still ambiguous, BuildingComplexName (against BUILDING_NAME). Any
/// result other than exactly one row returns null.
/// </summary>
public static class OpenserveBuildingMatcher
{
    public static OpenserveQualificationBuilding? Match(IReadOnlyList<OpenserveQualificationBuilding>? buildings, string? unitNumber, string? buildingComplexName)
    {
        if (buildings is null || buildings.Count == 0) return null;
        if (buildings.Count == 1) return buildings[0];

        var unitKey = Normalize(unitNumber);
        if (unitKey.Length == 0) return null;

        var byUnit = buildings.Where(b => Normalize(b.Num) == unitKey).ToList();
        if (byUnit.Count == 1) return byUnit[0];
        if (byUnit.Count == 0) return null;

        var nameKey = Normalize(buildingComplexName);
        if (nameKey.Length == 0) return null;

        var byName = byUnit.Where(b => Normalize(b.BuildingName) is { Length: > 0 } n && (n == nameKey || n.Contains(nameKey) || nameKey.Contains(n))).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Whether one candidate row's NUM is the customer's unit (same normalisation as <see cref="Match"/>).</summary>
    public static bool UnitMatches(OpenserveQualificationBuilding building, string? unitNumber)
    {
        var unitKey = Normalize(unitNumber);
        return unitKey.Length > 0 && Normalize(building.Num) == unitKey;
    }

    // "Unit 12", "#12", "12" and "Flat 12" all compare as "12"; "3F" and "3f" compare equal.
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var upper = value.Trim().ToUpperInvariant();
        foreach (var prefix in new[] { "UNIT", "FLAT", "APARTMENT", "APT", "NO.", "NO", "#" })
        {
            if (upper.StartsWith(prefix, StringComparison.Ordinal) && upper.Length > prefix.Length && !char.IsLetter(upper[prefix.Length]))
            {
                upper = upper[prefix.Length..];
                break;
            }
        }

        return new string(upper.Where(char.IsLetterOrDigit).ToArray());
    }
}

/// <summary>
/// Building/unit resolution, kept separate from AMID qualification. A valid
/// AMID is always stored; when Openserve returns several buildingInfo rows
/// for it and none matches the customer deterministically, the order's
/// building/unit is unresolved and Create Order is blocked until Admin picks
/// one of the rows Openserve returned (BLD_NUM_ID is never guessed). This
/// applies whatever the PropertyType says — including historical orders with
/// no PropertyType — because the qualification response decides.
/// </summary>
public static class OpenserveBuildingCandidates
{
    /// <summary>Bound on stored rows — a very large complex still fits comfortably.</summary>
    public const int MaxStoredCandidates = 1000;

    public const string MultipleUnitsReason = "Openserve found multiple units at this address. Confirm the customer's building/unit details before submitting the order.";

    // Written by the earlier qualification code when several rows came back,
    // before the candidate count was stored.
    private const string LegacyMultiMatchMarker = "building/unit matches were returned";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string? Serialize(IReadOnlyList<OpenserveQualificationBuilding>? buildings) =>
        buildings is null || buildings.Count == 0 ? null : JsonSerializer.Serialize(buildings.Take(MaxStoredCandidates).ToList(), JsonOptions);

    public static IReadOnlyList<OpenserveQualificationBuilding> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<OpenserveQualificationBuilding>();
        try
        {
            return JsonSerializer.Deserialize<List<OpenserveQualificationBuilding>>(json, JsonOptions) ?? new List<OpenserveQualificationBuilding>();
        }
        catch (JsonException)
        {
            return Array.Empty<OpenserveQualificationBuilding>();
        }
    }

    /// <summary>
    /// True when the order has an AMID but its building/unit is unresolved:
    /// several candidates, no BLD_NUM_ID chosen. Orders qualified before the
    /// count was stored are read from the note the old code left.
    /// </summary>
    public static bool NeedsResolution(Order order)
    {
        if (string.IsNullOrWhiteSpace(order.OpenserveAmId) || !string.IsNullOrWhiteSpace(order.OpenserveBuildingNumId)) return false;
        if (order.OpenserveBuildingCandidateCount is { } count) return count > 1;
        return order.OpenserveQualificationFailureReason?.Contains(LegacyMultiMatchMarker, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Records the rows Openserve returned and picks the customer's own row
    /// only when that is deterministic (a single row, or exactly one match on
    /// unit/building). Never touches the AMID.
    /// </summary>
    public static void Apply(Order order, IReadOnlyList<OpenserveQualificationBuilding>? buildings, int reportedCount, string? singleRowBuildingNumId)
    {
        var rows = buildings ?? Array.Empty<OpenserveQualificationBuilding>();
        var count = rows.Count > 0 ? rows.Count : reportedCount;
        order.OpenserveBuildingCandidateCount = count;
        order.OpenserveBuildingCandidatesJson = Serialize(rows);

        var matched = OpenserveBuildingMatcher.Match(rows, order.UnitNumber, order.BuildingComplexName);
        order.OpenserveBuildingNumId = matched?.BldNumId ?? (count <= 1 ? singleRowBuildingNumId : null);
        order.OpenserveBuildingName = matched?.BuildingName;
        order.OpenserveFloor = matched?.Floor;
        order.OpenserveUnit = matched?.Num;

        order.OpenserveQualificationFailureReason = count > 1 && matched is null
            ? string.IsNullOrWhiteSpace(order.UnitNumber)
                ? $"AMID captured, but {count} building/unit matches were returned and the order has no unit number to match — building details left blank pending unit confirmation."
                : $"AMID captured, but {count} building/unit matches were returned and none uniquely matched unit '{order.UnitNumber}' — building details left blank pending unit confirmation."
            : null;
    }
}

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

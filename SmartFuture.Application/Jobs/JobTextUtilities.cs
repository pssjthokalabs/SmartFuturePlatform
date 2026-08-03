using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartFuture.Application.Jobs;

// Shared, side-effect-free helpers for the Job Opportunities module.
// Kept in one place so the importer, the admin service, and the tests
// all agree on the fingerprint and slug recipes — a drift between them
// would silently break de-duplication.
public static class JobTextUtilities
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    // ─── De-duplication ───────────────────────────────────────────────
    //
    // Fingerprint = SHA-256( sourceUrl | title | company | location ),
    // each component lower-cased, whitespace-collapsed, and stripped of
    // punctuation that boards shuffle between crawls. Returned as a
    // 64-char hex string to match the UNIQUE column.
    //
    // Why not just SourceUrl: many boards publish several roles on ONE
    // article URL, and many rotate tracking query strings on the same
    // listing. Title+company+location disambiguates the first case;
    // normalising the URL handles the second.
    public static string ComputeFingerprint(string? sourceUrl, string? title, string? companyName, string? location)
    {
        var raw = string.Join('|', NormalizeUrl(sourceUrl), Normalize(title), Normalize(companyName), Normalize(location));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    // Lower-case, drop the fragment and common tracking query params,
    // and strip a trailing slash so "…/job/1?utm_source=x#top" and
    // "…/job/1/" collapse to the same key.
    public static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var trimmed = url.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return Normalize(trimmed);

        var query = uri.Query;
        if (!string.IsNullOrEmpty(query))
        {
            var kept = query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !IsTrackingParam(p))
                .ToArray();
            query = kept.Length == 0 ? string.Empty : "?" + string.Join('&', kept);
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Host}{path}{query}".ToLowerInvariant();
    }

    private static bool IsTrackingParam(string pair)
    {
        var key = pair.Split('=', 2)[0].ToLowerInvariant();
        return key.StartsWith("utm_", StringComparison.Ordinal)
            || key is "fbclid" or "gclid" or "msclkid" or "ref" or "source";
    }

    // Lower-case, collapse whitespace, drop punctuation. Used for the
    // fingerprint components and for loose text comparisons.
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    // ─── Slugs ────────────────────────────────────────────────────────
    //
    // "Senior .NET Developer (Cape Town)" → "senior-net-developer-cape-town".
    // Callers append a uniqueness suffix when the base collides — see
    // BuildUniqueSlug.
    public static string ToSlug(string? value, int maxLength = 200)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0) return "job";

        var slug = normalized.Replace(' ', '-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);

        slug = slug.Trim('-');
        if (slug.Length > maxLength) slug = slug[..maxLength].TrimEnd('-');
        return slug.Length == 0 ? "job" : slug;
    }

    // Deterministic collision handling: base, base-2, base-3, … The
    // caller supplies the "does this slug already exist" probe so the
    // helper stays free of DB dependencies (and unit-testable).
    public static string BuildUniqueSlug(string? title, string? companyName, Func<string, bool> exists)
    {
        var basis = string.IsNullOrWhiteSpace(companyName) ? title : $"{title} {companyName}";
        var baseSlug = ToSlug(basis, 200);
        if (!exists(baseSlug)) return baseSlug;

        for (var i = 2; i <= 200; i++)
        {
            var candidate = $"{baseSlug}-{i}";
            if (!exists(candidate)) return candidate;
        }

        // Pathological case — fall back to a guid tail, which cannot
        // collide in practice.
        return $"{baseSlug}-{Guid.NewGuid():N}"[..Math.Min(220, baseSlug.Length + 33)];
    }

    // ─── JSON list columns ────────────────────────────────────────────
    //
    // Tags / preferred categories / preferred locations are stored as a
    // JSON string array. Both directions are forgiving: bad JSON reads
    // as empty rather than throwing into a request path.
    public static IReadOnlyList<string> ReadStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            if (parsed is null) return Array.Empty<string>();
            return parsed.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static string? WriteStringList(IEnumerable<string>? values)
    {
        if (values is null) return null;
        var cleaned = values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
        return cleaned.Count == 0 ? null : JsonSerializer.Serialize(cleaned, JsonOptions);
    }

    // ─── Misc ─────────────────────────────────────────────────────────

    public static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Truncate on a word boundary where possible — used to synthesise a
    // card summary from a long description.
    public static string? BuildSummary(string? text, int maxLength = 300)
    {
        var cleaned = NullIfBlank(text);
        if (cleaned is null) return null;

        cleaned = CollapseWhitespace(cleaned);
        if (cleaned.Length <= maxLength) return cleaned;

        var cut = cleaned[..maxLength];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > maxLength / 2) cut = cut[..lastSpace];
        return cut.TrimEnd(' ', ',', ';', '.', '-') + "…";
    }

    public static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    // Human labels for enum values so the portal/app don't each maintain
    // their own mapping table.
    public static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
    }
}

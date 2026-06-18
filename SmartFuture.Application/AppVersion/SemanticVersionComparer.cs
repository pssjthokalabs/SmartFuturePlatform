namespace SmartFuture.Application.AppVersion;

/// <summary>
/// Safe dotted-numeric version compare ("1.0.10" &gt; "1.0.2"). Missing /
/// non-numeric segments are treated as 0 so a malformed value never throws.
/// Mirrors the mobile client's compareVersions so both ends agree.
/// </summary>
public static class SemanticVersionComparer
{
    /// <summary>-1 if a &lt; b, 1 if a &gt; b, 0 if equal.</summary>
    public static int Compare(string? a, string? b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var av = i < pa.Length ? pa[i] : 0;
            var bv = i < pb.Length ? pb[i] : 0;
            if (av != bv) return av < bv ? -1 : 1;
        }
        return 0;
    }

    public static bool IsLessThan(string? a, string? b) => Compare(a, b) < 0;

    private static int[] Parse(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return new[] { 0 };
        var parts = v.Split('.');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            result[i] = int.TryParse(parts[i].Trim(), out var n) && n > 0 ? n : 0;
        return result;
    }
}

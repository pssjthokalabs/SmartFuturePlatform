using System.Text.RegularExpressions;

namespace SmartFuture.Shared.Utilities;

/// <summary>
/// Single source of truth for detecting SmartFuture controlled TEST customer
/// accounts (the ones QA uses on LIVE without polluting real business data).
///
/// The ONLY emails that qualify are EXACTLY:
///     customer{n}@gmail.com   where n is an integer 1000–1999
/// Matching is case-insensitive. Nothing else is ever a test account — in
/// particular operator accounts such as developers@smartfuture.co.za and
/// vuyani@smartfuture.co.za are NOT test accounts (they don't match the
/// pattern, so they're excluded automatically).
///
/// This is the auto-detection rule used to SET the persisted
/// <c>User.IsTestAccount</c> flag at create time. Runtime behaviour
/// (stats exclusion, super-delete, magic OTP, payment override) keys off the
/// stored flag — NOT this regex — so the flag stays authoritative even if the
/// pattern is widened or retired later.
/// </summary>
public static class TestAccountPolicy
{
    // customer + (1000–1999) + @gmail.com. "1\d{3}" is exactly 1000–1999.
    // Anchored + case-insensitive. Compiled once.
    private static readonly Regex TestEmailRegex = new(
        @"^customer1\d{3}@gmail\.com$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Lowest test-account index (inclusive).</summary>
    public const int MinIndex = 1000;

    /// <summary>Highest test-account index (inclusive).</summary>
    public const int MaxIndex = 1999;

    /// <summary>
    /// True only when <paramref name="email"/> matches the reserved test
    /// pattern customer{1000-1999}@gmail.com (case-insensitive). Null / empty /
    /// any other address returns false.
    /// </summary>
    public static bool IsTestAccountEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        return TestEmailRegex.IsMatch(email.Trim());
    }
}

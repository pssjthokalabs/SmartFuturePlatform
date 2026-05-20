namespace SmartFuture.Shared.Utilities;

// Phase 43 — central normalizer used by user-create and user-update
// flows so a "phone already in use" check can match across formats
// (e.g. 0737942244 == +27737942244 == 27737942244 for SA numbers).
//
// We only normalize South African mobiles here — international support
// is out of scope for UAT. Anything that doesn't look like SA falls
// through to a digits-only canonical form so we still catch
// formatting-only duplicates without claiming to validate the country.
//
// Canonical output:  +27737942244  (E.164-style)
// Returns null when the input is blank or contains no usable digits.
public static class PhoneNumberNormalizer
{
    public const string DefaultCountryCode = "ZA";

    public static string? Normalize(string? phone, string defaultCountryCode = DefaultCountryCode)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        // Preserve a leading '+' so we know an explicit international
        // prefix was supplied; otherwise strip everything except digits.
        var trimmed = phone.Trim();
        var hasPlus = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        if (string.Equals(defaultCountryCode, "ZA", StringComparison.OrdinalIgnoreCase))
        {
            // +27 7xx xxx xxx → digits=27737942244 (11)
            if (digits.StartsWith("27") && digits.Length == 11)
                return "+" + digits;

            // 0737942244 → +27737942244
            if (!hasPlus && digits.StartsWith('0') && digits.Length == 10)
                return "+27" + digits[1..];

            // Bare 9 digits ("737942244") — treat as SA mobile minus leading 0.
            if (!hasPlus && digits.Length == 9)
                return "+27" + digits;
        }

        // Non-SA or non-matching shape — fall back to "+<digits>" if the
        // caller supplied a '+', otherwise digits-only. We still want a
        // stable canonical form so two stored copies of "+44 20 7946 0958"
        // and "+442079460958" collide on lookup.
        return hasPlus ? "+" + digits : digits;
    }
}

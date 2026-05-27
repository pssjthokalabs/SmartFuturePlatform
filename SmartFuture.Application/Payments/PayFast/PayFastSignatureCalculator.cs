using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SmartFuture.Application.Payments.PayFast;

/// <summary>
/// Pure, testable helper for PayFast MD5 signatures.
///
/// PayFast signature spec (https://developers.payfast.co.za/docs#step_2_signature):
///   1. Collect all non-blank parameter key=value pairs in submission order.
///   2. URL-encode each value using PHP urlencode semantics (spaces as +).
///   3. Concatenate as key1=val1&amp;key2=val2&amp;...
///   4. If a passphrase is configured, append &amp;passphrase={encoded passphrase}.
///   5. MD5-hash the resulting string → lowercase hex.
///
/// PayFast's own Node.js example confirms the encoding:
///   encodeURIComponent(val).replace(/%20/g, "+")
/// which maps to C#: Uri.EscapeDataString(val).Replace("%20", "+")
/// </summary>
public static class PayFastSignatureCalculator
{
    public static string GenerateSignature(IEnumerable<KeyValuePair<string, string>> parameters, string? passphrase)
    {
        var paramString = BuildParamString(parameters);
        if (!string.IsNullOrWhiteSpace(passphrase))
            paramString += $"&passphrase={PhpUrlEncode(passphrase.Trim())}";

        return Md5Lower(paramString);
    }

    public static string BuildParamString(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var pairs = new List<string>();
        foreach (var kv in parameters)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            pairs.Add($"{kv.Key}={PhpUrlEncode(kv.Value.Trim())}");
        }
        return string.Join("&", pairs);
    }

    public static string FormatAmount(decimal amount)
        => amount.ToString("F2", CultureInfo.InvariantCulture);

    public static bool SignaturesMatch(string expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        var e = expected.Trim().ToLowerInvariant();
        var a = actual.Trim().ToLowerInvariant();
        if (e.Length != a.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(e),
            Encoding.UTF8.GetBytes(a));
    }

    /// <summary>
    /// Matches PHP's urlencode(): RFC 1738 encoding where spaces
    /// become + and hex escapes are uppercase (%2F not %2f).
    /// Uri.EscapeDataString is RFC 3986 (spaces = %20, uppercase hex)
    /// so we only need to swap %20 → +.
    /// </summary>
    public static string PhpUrlEncode(string value)
        => Uri.EscapeDataString(value).Replace("%20", "+");

    private static string Md5Lower(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SmartFuture.Application.Payments.PayFast;

/// <summary>
/// Pure, testable helper for PayFast MD5 signatures.
///
/// PayFast signature spec (https://developers.payfast.co.za/docs#step_2_signature):
///   1. Collect all non-blank parameter key=value pairs in submission order.
///   2. URL-encode each value.
///   3. Concatenate as key1=val1&amp;key2=val2&amp;...
///   4. If a passphrase is configured, append &amp;passphrase={passphrase}.
///   5. MD5-hash the resulting string → lowercase hex.
/// </summary>
public static class PayFastSignatureCalculator
{
    public static string GenerateSignature(IEnumerable<KeyValuePair<string, string>> parameters, string? passphrase)
    {
        var pairs = new List<string>();
        foreach (var kv in parameters)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            pairs.Add($"{kv.Key}={Uri.EscapeDataString(kv.Value.Trim())}");
        }

        var paramString = string.Join("&", pairs);
        if (!string.IsNullOrWhiteSpace(passphrase))
            paramString += $"&passphrase={Uri.EscapeDataString(passphrase.Trim())}";

        return Md5Lower(paramString);
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

    private static string Md5Lower(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}

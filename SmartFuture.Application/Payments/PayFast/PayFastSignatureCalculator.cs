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
    /// ITN-correct signature for an INCOMING PayFast Instant Transaction
    /// Notification. Mirrors PayFast's published PHP example exactly:
    ///
    ///   $pfOutput = '';
    ///   foreach ($_POST as $key => $val) {
    ///       if ($key !== 'signature') {
    ///           $pfOutput .= $key . '=' . urlencode($val) . '&amp;';
    ///       }
    ///   }
    ///   $pfOutput = substr($pfOutput, 0, -1);
    ///   if (!empty($passPhrase)) {
    ///       $pfOutput .= '&amp;passphrase=' . urlencode($passPhrase);
    ///   }
    ///   $signature = md5($pfOutput);
    ///
    /// Differences from <see cref="GenerateSignature"/> (which is for
    /// the OUTGOING redirect):
    ///   1. Empty fields are INCLUDED as <c>key=</c>. PayFast iterates
    ///      every posted field; our outgoing variant dropped empties
    ///      which caused live ITNs to fail signature with R−2.70
    ///      <c>amount_fee</c> + empty customs (2026-06 UAT incident).
    ///   2. Field order is the order PayFast posted (preserved by
    ///      <c>ReadFormAsync</c>'s <c>FormCollection</c>), not the
    ///      order we use to sign the outgoing redirect.
    ///   3. Values are NOT trimmed. PayFast's PHP does not trim, and
    ///      our hash must match byte-for-byte.
    ///   4. <c>signature</c> is the only excluded field. Negative
    ///      values (e.g. <c>amount_fee=-2.70</c>) are included verbatim.
    /// </summary>
    /// <param name="postedFields">Fields PayFast posted, in the order
    ///   received. Caller MUST preserve order (use a List of
    ///   KeyValuePair, not a re-ordered dictionary).</param>
    /// <param name="passphrase">PayFast merchant passphrase or null.
    ///   Trimmed before encoding — leading/trailing whitespace in the
    ///   stored secret is a recurring footgun.</param>
    /// <param name="diagnostics">Out parameter populated with the
    ///   redacted base string + chosen field order. Safe to include
    ///   in a forensic file — the actual passphrase is masked.</param>
    public static string GenerateItnSignature(
        IReadOnlyList<KeyValuePair<string, string>> postedFields,
        string? passphrase,
        out PayFastItnSignatureDebug diagnostics)
    {
        if (postedFields is null) throw new ArgumentNullException(nameof(postedFields));

        var pairs = new List<string>(postedFields.Count);
        var fieldOrder = new List<string>(postedFields.Count);
        foreach (var kv in postedFields)
        {
            if (string.Equals(kv.Key, "signature", StringComparison.OrdinalIgnoreCase)) continue;
            // INCLUDE empty values as `key=`. NO trim.
            var value = kv.Value ?? string.Empty;
            pairs.Add($"{kv.Key}={PhpUrlEncode(value)}");
            fieldOrder.Add(kv.Key);
        }
        var paramString = string.Join("&", pairs);

        var passphraseConfigured = !string.IsNullOrWhiteSpace(passphrase);
        var withPassphrase = passphraseConfigured
            ? $"{paramString}&passphrase={PhpUrlEncode(passphrase!.Trim())}"
            : paramString;

        diagnostics = new PayFastItnSignatureDebug
        {
            FieldNamesInOrder = fieldOrder,
            BaseStringRedacted = passphraseConfigured
                ? $"{paramString}&passphrase=[REDACTED]"
                : paramString,
            PassphraseConfigured = passphraseConfigured,
            Algorithm = "itn-posted-order-include-empties",
        };

        return Md5Lower(withPassphrase);
    }

    /// <summary>
    /// Matches PHP's <c>urlencode()</c>. .NET's
    /// <see cref="Uri.EscapeDataString(string)"/> is RFC 3986 — PHP's
    /// <c>urlencode</c> is RFC 1738, which differs in:
    ///   - space: PHP=+, RFC 3986=%20
    ///   - tilde: PHP=%7E, RFC 3986=~
    ///   - ! * ' ( ) : PHP encodes, RFC 3986 leaves verbatim
    /// PayFast signs with PHP semantics, so we must match exactly.
    /// </summary>
    public static string PhpUrlEncode(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return Uri.EscapeDataString(value)
            .Replace("%20", "+")
            .Replace("!", "%21")
            .Replace("*", "%2A")
            .Replace("'", "%27")
            .Replace("(", "%28")
            .Replace(")", "%29")
            .Replace("~", "%7E");
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

/// <summary>
/// Per-request signature debug info populated by
/// <see cref="PayFastSignatureCalculator.GenerateItnSignature"/>. Safe
/// to attach to forensic output — the passphrase value is masked.
/// </summary>
public class PayFastItnSignatureDebug
{
    public List<string> FieldNamesInOrder { get; set; } = new();
    public string BaseStringRedacted { get; set; } = string.Empty;
    public bool PassphraseConfigured { get; set; }
    public string Algorithm { get; set; } = string.Empty;
}

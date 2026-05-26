using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SmartFuture.Application.Payments.Ozow;

/// <summary>
/// Pure, testable helper for the Ozow PostPaymentRequest / Notify
/// hashes. Kept static + dependency-free so it can be unit tested
/// without DI or HTTP plumbing.
///
/// <para>
/// Hash algorithm (per Ozow Payments API docs — verified against the
/// official PHP sample
/// https://github.com/wdtheprovider/ozpay/blob/master/payment.php):
/// </para>
/// <code>
///   hash = SHA512( lowercase( concat(fields...) + PrivateKey ) )
/// </code>
/// <para>
/// The fields are concatenated in EXACT order documented by Ozow
/// (different for request vs notify — see methods below). The whole
/// string — including the PrivateKey — is lowercased before hashing.
/// The output is the lowercase hex string of the SHA512 bytes.
/// </para>
///
/// <para>
/// IMPORTANT: Decimal amounts MUST be formatted with two decimal
/// places using InvariantCulture (e.g. "12.50" not "12,50") because
/// the same string is hashed on both ends and a locale mismatch will
/// silently break verification.
/// </para>
/// </summary>
public static class OzowHashCalculator
{
    /// <summary>
    /// Request hash for PostPaymentRequest. Field order per Ozow docs:
    /// SiteCode, CountryCode, CurrencyCode, Amount, TransactionReference,
    /// BankReference, CancelUrl, ErrorUrl, SuccessUrl, NotifyUrl, IsTest,
    /// then PrivateKey is appended.
    /// </summary>
    public static string BuildRequestHash(
        string siteCode,
        string countryCode,
        string currencyCode,
        decimal amount,
        string transactionReference,
        string bankReference,
        string cancelUrl,
        string errorUrl,
        string successUrl,
        string notifyUrl,
        bool isTest,
        string privateKey)
    {
        var concatenated = string.Concat(
            siteCode,
            countryCode,
            currencyCode,
            FormatAmount(amount),
            transactionReference,
            bankReference,
            cancelUrl,
            errorUrl,
            successUrl,
            notifyUrl,
            FormatBool(isTest),
            privateKey);
        return Sha512Lower(concatenated.ToLowerInvariant());
    }

    /// <summary>
    /// Response/notify hash. Field order per Ozow docs:
    /// SiteCode, TransactionId, TransactionReference, Amount, Status,
    /// Optional1, Optional2, Optional3, Optional4, Optional5,
    /// CurrencyCode, IsTest, StatusMessage, then PrivateKey is
    /// appended. Empty/null optional fields contribute an empty string.
    /// </summary>
    public static string BuildResponseHash(
        string siteCode,
        string transactionId,
        string transactionReference,
        decimal amount,
        string status,
        string? optional1,
        string? optional2,
        string? optional3,
        string? optional4,
        string? optional5,
        string currencyCode,
        bool isTest,
        string? statusMessage,
        string privateKey)
    {
        var concatenated = string.Concat(
            siteCode,
            transactionId,
            transactionReference,
            FormatAmount(amount),
            status,
            optional1 ?? string.Empty,
            optional2 ?? string.Empty,
            optional3 ?? string.Empty,
            optional4 ?? string.Empty,
            optional5 ?? string.Empty,
            currencyCode,
            FormatBool(isTest),
            statusMessage ?? string.Empty,
            privateKey);
        return Sha512Lower(concatenated.ToLowerInvariant());
    }

    /// <summary>
    /// Constant-time equality so a timing oracle can't be used to leak
    /// the expected hash. Both inputs are normalised to lowercase first
    /// because Ozow's docs return the hash as lowercase hex but some
    /// fields-in-the-wild ship as uppercase.
    /// </summary>
    public static bool HashesMatch(string expected, string? actual)
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
    /// Currency amount formatter used in the hash input. ToString("F2",
    /// InvariantCulture) gives "0.50" / "150.00" / "1234.56" — matches
    /// the JSON body's amount serialisation, so both sides hash the
    /// same string.
    /// </summary>
    public static string FormatAmount(decimal amount)
        => amount.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatBool(bool value) => value ? "true" : "false";

    private static string Sha512Lower(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA512.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}

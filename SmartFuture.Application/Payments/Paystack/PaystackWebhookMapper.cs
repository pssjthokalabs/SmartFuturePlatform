using System.Security.Cryptography;
using System.Text;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Pure, side-effect-free mapper for Paystack webhook payloads.
/// Extracted from <see cref="PaystackNotifyHandler"/> so signature
/// verification + event/status → <see cref="PaymentStatus"/> translation
/// are unit-testable without loading the full 9-dependency handler.
///
/// Full-flow tests (unknown reference, apply-service invoked exactly
/// once, duplicate-event guard) still live inside the handler and
/// require heavier integration harnesses; the pure decisions here cover
/// the parts of the translator that carry the most direct security risk
/// (signature bypass, wrong status mapping).
/// </summary>
public static class PaystackWebhookMapper
{
    /// <summary>
    /// Paystack canonical event → SmartFuture payment status. The
    /// handler currently only ACTS on <c>charge.success</c>; every
    /// other event is acknowledged as non-actionable.
    /// </summary>
    public static PaymentStatus? MapEventToPaymentStatus(string? eventName, string? dataStatus)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return null;
        if (!string.Equals(eventName.Trim(), "charge.success", StringComparison.OrdinalIgnoreCase))
            return null;

        // Within charge.success Paystack's data.status is normally
        // "success" but we also accept the older short form for safety.
        var status = dataStatus?.Trim().ToLowerInvariant();
        return status switch
        {
            "success" => PaymentStatus.Completed,
            null or ""  => PaymentStatus.Completed, // charge.success without a nested status still means success
            "failed" => PaymentStatus.Failed,
            "abandoned" => PaymentStatus.Failed,
            "reversed" => PaymentStatus.Reversed,
            _ => null
        };
    }

    /// <summary>
    /// Convert a ZAR decimal amount to Paystack kobo/cent subunits
    /// (integer). Mirrors the private <c>ToSubunits</c> helper the
    /// notify handler + initiator both use so the two sides always
    /// agree on the wire amount.
    /// </summary>
    public static long ToSubunits(decimal zar)
        => (long)Math.Round(zar * 100m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Verify Paystack's HMAC-SHA512 webhook signature (the
    /// <c>x-paystack-signature</c> header).
    ///
    /// Constant-time compare via
    /// <see cref="CryptographicOperations.FixedTimeEquals(byte[], byte[])"/>.
    /// Paystack's PHP + Node.js examples both emit LOWERCASE hex, while
    /// .NET's <see cref="Convert.ToHexString(byte[])"/> emits UPPERCASE.
    /// We normalise BOTH sides to uppercase before comparing so either
    /// case works — a hex string is case-insensitive by definition and
    /// the previous implementation's "case-insensitive fallback" (guarded
    /// by a length mismatch that never occurs for same-length hex) was
    /// dead code that would silently reject legitimate lower-case
    /// signatures. See Phase 4 tests for the regression coverage.
    /// </summary>
    public static bool IsWebhookSignatureValid(string? rawBody, string? headerValue, string? secretKey)
    {
        if (string.IsNullOrEmpty(rawBody)) return false;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        if (string.IsNullOrWhiteSpace(secretKey)) return false;

        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));

        // Convert.ToHexString → UPPERCASE. Normalise the header value to
        // UPPERCASE too so LOWER + UPPER + Mixed hex all match. The
        // ToUpperInvariant on an ASCII hex string is a length-preserving
        // operation, so the constant-time compare below still runs against
        // equal-length spans (defence against timing leaks on length).
        var expectedHex = Convert.ToHexString(hash);
        var normalizedHeader = headerValue.Trim().ToUpperInvariant();

        var headerBytes = Encoding.ASCII.GetBytes(normalizedHeader);
        var expectedBytes = Encoding.ASCII.GetBytes(expectedHex);
        if (headerBytes.Length != expectedBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(headerBytes, expectedBytes);
    }

    /// <summary>
    /// Cross-check the ITN amount against server state. The handler
    /// pulls <c>PaymentInitiation.Amount</c> (or <c>Payment.Amount</c>)
    /// and compares to Paystack's kobo/cents.
    /// </summary>
    public static bool AmountsMatch(decimal expectedZar, long reportedSubunits)
        => ToSubunits(expectedZar) == reportedSubunits;
}

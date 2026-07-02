using System.Security.Cryptography;
using System.Text;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Tests.Billing;

// Phase-4 tests for PaystackWebhookMapper — the extracted pure seam
// covering HMAC-SHA512 signature verification, Paystack event/status →
// PaymentStatus translation, and the kobo/cent subunit conversion.
//
// Full-flow tests (unknown reference, apply-service-called-once,
// duplicate event guard) still live in the handler — see report for
// the Phase 5 harness that could unlock them.
public class PaystackWebhookMapperTests
{
    private const string SecretKey = "sk_test_XXXXXXXXXXXXXXXXXXXXXXXX";

    private static string HmacSha512Hex(string body, string secret)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash);
    }

    // ─── Event → PaymentStatus ─────────────────────────────────────

    [Fact]
    public void PaystackWebhook_ValidChargeSuccess_MapsToCompletedPaymentStatus()
    {
        PaystackWebhookMapper.MapEventToPaymentStatus("charge.success", "success")
            .Should().Be(PaymentStatus.Completed);
    }

    [Fact]
    public void PaystackWebhook_ChargeSuccess_MissingNestedStatus_StillCompleted()
    {
        // Some Paystack payloads omit data.status inside charge.success.
        // Treat as success — the top-level event is authoritative.
        PaystackWebhookMapper.MapEventToPaymentStatus("charge.success", null)
            .Should().Be(PaymentStatus.Completed);
        PaystackWebhookMapper.MapEventToPaymentStatus("charge.success", "")
            .Should().Be(PaymentStatus.Completed);
    }

    [Theory]
    [InlineData("failed", PaymentStatus.Failed)]
    [InlineData("abandoned", PaymentStatus.Failed)]
    [InlineData("reversed", PaymentStatus.Reversed)]
    public void PaystackWebhook_FailedCharge_MapsToFailedOrReversed(string dataStatus, PaymentStatus expected)
    {
        PaystackWebhookMapper.MapEventToPaymentStatus("charge.success", dataStatus)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("customer.identification.success")]
    [InlineData("transfer.success")]
    [InlineData("subscription.create")]
    [InlineData("")]
    [InlineData(null)]
    public void PaystackWebhook_NonActionableEvent_ReturnsNull(string? eventName)
    {
        PaystackWebhookMapper.MapEventToPaymentStatus(eventName, "success")
            .Should().BeNull();
    }

    [Fact]
    public void PaystackWebhook_CaseInsensitiveEventMatch()
    {
        PaystackWebhookMapper.MapEventToPaymentStatus("Charge.Success", "success")
            .Should().Be(PaymentStatus.Completed);
    }

    // ─── ToSubunits ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 100)]
    [InlineData(100, 10_000)]
    [InlineData(349.77, 34_977)]
    [InlineData(0.01, 1)]
    [InlineData(1234.56, 123_456)]
    public void PaystackWebhook_ToSubunits_ConvertsZarToKoboCents(decimal zar, long expected)
    {
        PaystackWebhookMapper.ToSubunits(zar).Should().Be(expected);
    }

    [Fact]
    public void PaystackWebhook_AmountsMatch_ExactMatch()
    {
        PaystackWebhookMapper.AmountsMatch(expectedZar: 349.77m, reportedSubunits: 34_977).Should().BeTrue();
    }

    [Fact]
    public void PaystackWebhook_AmountMismatch_IsRejected()
    {
        // Real amount mismatch → reject.
        PaystackWebhookMapper.AmountsMatch(expectedZar: 349.77m, reportedSubunits: 10_000).Should().BeFalse();
        PaystackWebhookMapper.AmountsMatch(expectedZar: 349.77m, reportedSubunits: 34_978).Should().BeFalse(
            "one-cent drift is a mismatch — Paystack is authoritative on subunits");
    }

    // ─── HMAC signature verification ──────────────────────────────

    [Fact]
    public void PaystackWebhook_ValidSignature_Accepted()
    {
        var body = "{\"event\":\"charge.success\",\"data\":{\"reference\":\"ref-1\",\"amount\":34977,\"status\":\"success\"}}";
        var sig = HmacSha512Hex(body, SecretKey);

        PaystackWebhookMapper.IsWebhookSignatureValid(body, sig, SecretKey).Should().BeTrue();
    }

    [Fact]
    public void PaystackWebhook_ValidSignature_LowercaseHexAccepted()
    {
        var body = "{\"event\":\"charge.success\",\"data\":{\"reference\":\"ref-1\"}}";
        var sig = HmacSha512Hex(body, SecretKey).ToLowerInvariant();

        PaystackWebhookMapper.IsWebhookSignatureValid(body, sig, SecretKey).Should().BeTrue(
            "Paystack docs use lower-case hex — must be accepted");
    }

    [Fact]
    public void PaystackWebhook_InvalidSignature_IsRejected()
    {
        var body = "{\"event\":\"charge.success\",\"data\":{\"reference\":\"ref-1\",\"amount\":34977}}";
        var wrongSig = HmacSha512Hex(body, "wrong-secret-key");

        PaystackWebhookMapper.IsWebhookSignatureValid(body, wrongSig, SecretKey).Should().BeFalse();
    }

    [Fact]
    public void PaystackWebhook_TamperedBody_IsRejected()
    {
        // Sign the original, then tamper the amount field. Must reject.
        var originalBody = "{\"event\":\"charge.success\",\"data\":{\"reference\":\"ref-1\",\"amount\":34977}}";
        var tamperedBody = originalBody.Replace("34977", "99999");
        var sig = HmacSha512Hex(originalBody, SecretKey);

        PaystackWebhookMapper.IsWebhookSignatureValid(tamperedBody, sig, SecretKey).Should().BeFalse(
            "changing even one byte in the body must invalidate the signature");
    }

    [Fact]
    public void PaystackWebhook_MalformedPayload_DoesNotCrash()
    {
        // Signature check runs before JSON parse — even garbage bodies
        // must return false, never throw.
        var body = "not-valid-json-at-all }";
        PaystackWebhookMapper.IsWebhookSignatureValid(body, "abc123", SecretKey)
            .Should().BeFalse();
        // With correct signature on garbage: signature verifies but
        // downstream JSON parse would fail (handler handles that separately).
        var sig = HmacSha512Hex(body, SecretKey);
        PaystackWebhookMapper.IsWebhookSignatureValid(body, sig, SecretKey).Should().BeTrue(
            "signature check is content-agnostic — the parsing gate lives downstream");
    }

    [Theory]
    [InlineData(null, "some-sig", SecretKey)]
    [InlineData("", "some-sig", SecretKey)]
    [InlineData("body", null, SecretKey)]
    [InlineData("body", "", SecretKey)]
    [InlineData("body", "   ", SecretKey)]
    [InlineData("body", "some-sig", null)]
    [InlineData("body", "some-sig", "")]
    [InlineData("body", "some-sig", "   ")]
    public void PaystackWebhook_MissingInputs_FailSafe(string? body, string? sig, string? secret)
    {
        PaystackWebhookMapper.IsWebhookSignatureValid(body, sig, secret).Should().BeFalse();
    }

    [Fact]
    public void PaystackWebhook_MismatchedLengthSignature_IsRejected()
    {
        // Half-length hex signature — can't possibly be right.
        var body = "{\"a\":1}";
        var validSig = HmacSha512Hex(body, SecretKey);
        var truncated = validSig[..64];

        PaystackWebhookMapper.IsWebhookSignatureValid(body, truncated, SecretKey).Should().BeFalse();
    }
}

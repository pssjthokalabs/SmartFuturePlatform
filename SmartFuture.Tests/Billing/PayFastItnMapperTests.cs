using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Tests.Billing;

// Phase-4 tests for PayFastItnMapper — the pure extracted seam covering
// PayFast's payment_status → PaymentStatus translation, amount tolerance,
// and merchant-id match logic. The signature-verification side is
// covered by PayFastSignatureCalculator (already pure), so tests for
// signature validity live in PayFastSignatureCalculatorTests.
public class PayFastItnMapperTests
{
    // ─── Status mapping ────────────────────────────────────────────

    [Theory]
    [InlineData("COMPLETE", PaymentStatus.Completed)]
    [InlineData("complete", PaymentStatus.Completed)]   // case-insensitive
    [InlineData("  COMPLETE  ", PaymentStatus.Completed)] // trimmed
    [InlineData("FAILED", PaymentStatus.Failed)]
    [InlineData("failed", PaymentStatus.Failed)]
    public void PayFastItn_ValidPaidPayload_MapsToCorrectPaymentStatus(string raw, PaymentStatus expected)
    {
        PayFastItnMapper.MapPaymentStatus(raw).Should().Be(expected);
    }

    [Fact]
    public void PayFastItn_FailedPayload_MapsToFailedPaymentStatus()
    {
        PayFastItnMapper.MapPaymentStatus("FAILED").Should().Be(PaymentStatus.Failed);
    }

    [Fact]
    public void PayFastItn_PendingPayload_MapsToNull_HandlerIgnores()
    {
        PayFastItnMapper.MapPaymentStatus("PENDING").Should().BeNull(
            "PayFast PENDING means the customer hasn't finished the flow — the handler ignores it rather than transitioning the payment status");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("UNKNOWN_STATUS")]
    [InlineData("CANCELLED")]  // not documented, treat as unknown
    public void PayFastItn_UnknownOrEmptyStatus_MapsToNull(string? raw)
    {
        PayFastItnMapper.MapPaymentStatus(raw).Should().BeNull();
    }

    // ─── Amount tolerance ─────────────────────────────────────────

    [Fact]
    public void PayFastItn_AmountMatch_ExactMatch()
    {
        PayFastItnMapper.AmountsMatch(expectedZar: 349.77m, reportedAmountGross: 349.77m)
            .Should().BeTrue();
    }

    [Fact]
    public void PayFastItn_AmountMatch_OneCentTolerance()
    {
        // Historical PayFast representation drift — 0.01 tolerance
        // stops it triggering a signature-verified ITN rejection.
        PayFastItnMapper.AmountsMatch(expectedZar: 349.77m, reportedAmountGross: 349.78m)
            .Should().BeTrue();
        PayFastItnMapper.AmountsMatch(expectedZar: 349.77m, reportedAmountGross: 349.76m)
            .Should().BeTrue();
    }

    [Fact]
    public void PayFastItn_AmountMismatch_IsRejected()
    {
        // A real mismatch (over the tolerance) is a hard-reject —
        // the handler refuses the ITN as tampered.
        PayFastItnMapper.AmountsMatch(expectedZar: 349.77m, reportedAmountGross: 100.00m)
            .Should().BeFalse();
    }

    [Fact]
    public void PayFastItn_AmountMismatch_LargeDrift_IsRejected()
    {
        // 10 rand off — miles outside tolerance.
        PayFastItnMapper.AmountsMatch(expectedZar: 349.77m, reportedAmountGross: 359.77m)
            .Should().BeFalse();
    }

    // ─── Merchant check ───────────────────────────────────────────

    [Fact]
    public void PayFastItn_MerchantMatches_HappyPath()
    {
        PayFastItnMapper.MerchantMatches("10000100", "10000100").Should().BeTrue();
    }

    [Fact]
    public void PayFastItn_MerchantMatches_LeadingTrailingWhitespaceTolerated()
    {
        PayFastItnMapper.MerchantMatches(" 10000100 ", "10000100").Should().BeTrue();
    }

    [Fact]
    public void PayFastItn_MerchantMismatch_HardReject()
    {
        PayFastItnMapper.MerchantMatches("10000100", "20000200").Should().BeFalse(
            "an ITN with a different merchant_id means the payment settled on a different account — hard reject");
    }

    [Theory]
    [InlineData("", "10000100")]
    [InlineData("10000100", "")]
    [InlineData(null, "10000100")]
    [InlineData("10000100", null)]
    [InlineData("   ", "10000100")]
    public void PayFastItn_MerchantMissing_FailsSafe(string? configured, string? reported)
    {
        PayFastItnMapper.MerchantMatches(configured, reported).Should().BeFalse(
            "missing/empty merchant_id on either side must not pass the guard");
    }
}

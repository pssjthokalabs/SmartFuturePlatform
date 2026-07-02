using SmartFuture.Application.Billing.ProRata;

namespace SmartFuture.Tests.Billing;

// Verifies the SmartFuture pro-rata formula end-to-end.
//
// Rules under test (locked in ProRataCalculator.cs):
//   proRata = round(monthlyPrice × billableDays / 30, 2, AwayFromZero)
//   period  = [startDate (inclusive), nextBillingDate (exclusive)]
//   billing-day 30 clamps to the last day of short months (28/29 Feb).
//
// All tests use fixed dates and deterministic UTC values — no clock,
// no timezone, no rounding surprises.
public class ProRataCalculatorTests
{
    // ─── NextBillingDate ─────────────────────────────────────────────

    [Fact]
    public void NextBillingDate_JoinBeforeBillingDay_ReturnsThisMonth()
    {
        var start = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var next = ProRataCalculator.NextBillingDate(start, billingDay: 15);

        next.Should().Be(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void NextBillingDate_JoinAfterBillingDay_ReturnsNextMonth()
    {
        var start = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

        var next = ProRataCalculator.NextBillingDate(start, billingDay: 15);

        next.Should().Be(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void NextBillingDate_JoinOnBillingDay_ReturnsSameDay_EmptyProRataWindow()
    {
        // Same-day edge case — customer joins on their billing day, so the
        // pro-rata window is empty. Callers must SKIP writing the ProRata
        // line item when billable days == 0.
        var start = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        var next = ProRataCalculator.NextBillingDate(start, billingDay: 15);

        next.Should().Be(start);
        ProRataCalculator.ComputeBillableDays(start, 15).Should().Be(0);
        ProRataCalculator.ComputeProRataAmount(monthlyPrice: 1499m, start, 15).Should().Be(0m);
    }

    [Fact]
    public void NextBillingDate_BillingDay30_InFebruary_NonLeap_ClampsToFeb28()
    {
        // 2026 is a common year; billing day 30 in a shorter month
        // resolves to the last day of that month.
        var start = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);

        var next = ProRataCalculator.NextBillingDate(start, billingDay: 30);

        next.Should().Be(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void NextBillingDate_BillingDay30_InLeapFebruary_ClampsToFeb29()
    {
        // 2028 is a leap year — Feb 29 is the effective billing day.
        var start = new DateTime(2028, 2, 15, 0, 0, 0, DateTimeKind.Utc);

        var next = ProRataCalculator.NextBillingDate(start, billingDay: 30);

        next.Should().Be(new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void NextBillingDate_JoinFebLastDay_BillingDay30_ReturnsSameDay_EmptyWindow()
    {
        // Customer joins on Feb 28 (non-leap) — that IS their billing
        // day in Feb because 30 clamps to 28. Empty pro-rata window.
        var start = new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc);

        var next = ProRataCalculator.NextBillingDate(start, billingDay: 30);

        next.Should().Be(start);
    }

    [Fact]
    public void NextBillingDate_JoinJan31_BillingDay30_ReturnsFeb28_NonLeap()
    {
        // Jan 31 is AFTER the effective billing day 30 (Jan has 31
        // days) — so we roll to Feb, clamped to 28.
        var start = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        var next = ProRataCalculator.NextBillingDate(start, billingDay: 30);

        next.Should().Be(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(-1)]
    public void NextBillingDate_InvalidBillingDay_Throws(int billingDay)
    {
        var start = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var act = () => ProRataCalculator.NextBillingDate(start, billingDay);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── ComputeProRataAmount ────────────────────────────────────────

    [Fact]
    public void ComputeProRataAmount_BillingDay15_FromJune4_Charges11Days()
    {
        // 4 June → 15 June exclusive = 4,5,6,7,8,9,10,11,12,13,14 = 11 days
        // 1499 × 11 / 30 = 549.6333… → rounds AwayFromZero → 549.63
        var start = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var amount = ProRataCalculator.ComputeProRataAmount(monthlyPrice: 1499m, start, billingDay: 15);

        amount.Should().Be(549.63m);
    }

    [Fact]
    public void ComputeProRataAmount_BillingDay25_FromJune22_ChargesThreeDays()
    {
        // 22 June → 25 June exclusive = 22, 23, 24 = 3 days
        // 900 × 3 / 30 = 90.00
        var start = new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

        var amount = ProRataCalculator.ComputeProRataAmount(monthlyPrice: 900m, start, billingDay: 25);

        amount.Should().Be(90.00m);
    }

    [Fact]
    public void ComputeProRataAmount_MonthEnd_February_ClampsToLastDay()
    {
        // 15 Feb 2026 → 28 Feb exclusive = 13 days (15..27)
        // 1499 × 13 / 30 = 649.5666… → 649.57
        var start = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);

        var amount = ProRataCalculator.ComputeProRataAmount(monthlyPrice: 1499m, start, billingDay: 30);

        amount.Should().Be(649.57m);
    }

    [Fact]
    public void ComputeProRataAmount_LeapFebruary_MonthEnd_ChargesOneExtraDay()
    {
        // 15 Feb 2028 → 29 Feb exclusive = 14 days
        // 1499 × 14 / 30 = 699.5333… → 699.53
        var start = new DateTime(2028, 2, 15, 0, 0, 0, DateTimeKind.Utc);

        var amount = ProRataCalculator.ComputeProRataAmount(monthlyPrice: 1499m, start, billingDay: 30);

        amount.Should().Be(699.53m);
    }

    [Fact]
    public void ComputeProRataAmount_SameDayStart_ReturnsZero()
    {
        // Customer picks billing day 25, joins ON the 25th.
        var start = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc);

        var amount = ProRataCalculator.ComputeProRataAmount(monthlyPrice: 1499m, start, billingDay: 25);

        amount.Should().Be(0m);
    }

    [Fact]
    public void ComputeProRataAmount_ZeroMonthly_ReturnsZero()
    {
        // Free packages (or promo tiers) should never invoice pro-rata
        // even if the window is non-empty.
        var start = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var amount = ProRataCalculator.ComputeProRataAmount(monthlyPrice: 0m, start, billingDay: 15);

        amount.Should().Be(0m);
    }

    [Fact]
    public void ComputeProRataAmount_NegativeMonthly_ReturnsZero()
    {
        // Defensive — negative prices shouldn't ever hit this call,
        // but pass through as 0 rather than a negative invoice line.
        var start = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var amount = ProRataCalculator.ComputeProRataAmount(monthlyPrice: -100m, start, billingDay: 15);

        amount.Should().Be(0m);
    }

    [Fact]
    public void ComputeProRataAmount_RoundsToTwoDecimals_AwayFromZero()
    {
        // 999.99 × 7 / 30 = 233.331 → rounds to 233.33
        // (banker's rounding would give the same here; the away-from-zero
        // guarantee bites on the .5 boundary — covered below).
        var start = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);

        var amount = ProRataCalculator.ComputeProRataAmount(monthlyPrice: 999.99m, start, billingDay: 15);

        amount.Should().Be(233.33m);
    }

    [Fact]
    public void ComputeProRataAmount_MidpointRoundsAwayFromZero_NotBanker()
    {
        // Contrive a midpoint case: 1.50 × 1 / 30 = 0.05 exactly, but
        // 1.05 × 1 / 30 = 0.035 → 0.04 (AwayFromZero) vs 0.03 (banker).
        var start = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);

        var amount = ProRataCalculator.ComputeProRataAmount(monthlyPrice: 1.05m, start, billingDay: 15);

        amount.Should().Be(0.04m); // AwayFromZero, matches Round(0.035, 2, AwayFromZero)
    }

    // ─── Quote ──────────────────────────────────────────────────────

    [Fact]
    public void Quote_FullBundle_ExposesPeriodAndBillableDays()
    {
        var start = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var quote = ProRataCalculator.Quote(monthlyPrice: 1499m, start, billingDay: 15);

        quote.StartDateUtc.Should().Be(start);
        quote.NextBillingDateUtc.Should().Be(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        quote.BillableDays.Should().Be(11);
        quote.MonthlyPrice.Should().Be(1499m);
        quote.ProRataAmount.Should().Be(549.63m);
    }

    [Fact]
    public void Quote_SameDayStart_ReturnsEmptyWindow()
    {
        var start = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        var quote = ProRataCalculator.Quote(monthlyPrice: 1499m, start, billingDay: 15);

        quote.BillableDays.Should().Be(0);
        quote.ProRataAmount.Should().Be(0m);
    }

    // ─── Description formatting ─────────────────────────────────────

    [Fact]
    public void FormatProRataDescription_UsesLastBillableDay()
    {
        var start = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);
        var next  = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        var description = ProRataCalculator.FormatProRataDescription(start, next);

        // Last billable day = nextBillingDate - 1 day = 14 Jun.
        description.Should().Contain("4 Jun 2026");
        description.Should().Contain("14 Jun 2026");
        description.Should().StartWith("Pro-rata service charge:");
    }

    [Fact]
    public void FormatMonthlyDescription_RendersInclusiveRange()
    {
        var start = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

        var description = ProRataCalculator.FormatMonthlyDescription(start, end);

        description.Should().Contain("15 Jun 2026");
        description.Should().Contain("14 Jul 2026");
        description.Should().StartWith("Monthly service charge:");
    }

    // ─── Business scenarios exercising the calculator holistically ──

    [Fact]
    public void SecurityCheckout_JoinJune4_BillingDay15_Charges549Point63()
    {
        // Documented Security business scenario from the spec:
        //   Customer joins on the 4th, chooses billing day 15.
        //   Monthly R1499 → pro-rata 549.63 for the 11-day window.
        var quote = ProRataCalculator.Quote(1499m, new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc), 15);

        quote.ProRataAmount.Should().Be(549.63m);
        quote.BillableDays.Should().Be(11);
    }

    [Fact]
    public void FibrePostActivation_ActivationJune8_BillingDay15_Charges349Point77()
    {
        // Documented Fibre business scenario from the spec:
        //   Installation completed 8 Jun. Billing day 15.
        //   Pro-rata invoice generated at admin activation.
        //   Monthly R1499 → pro-rata for [8 Jun, 15 Jun) = 7 days
        //   1499 × 7 / 30 = 349.7666… → 349.77
        var quote = ProRataCalculator.Quote(1499m, new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc), 15);

        quote.ProRataAmount.Should().Be(349.77m);
        quote.BillableDays.Should().Be(7);
    }

    [Fact]
    public void VariantPriceUsed_WhenSelectedVariantOverridesPackage()
    {
        // The calculator itself is variant-agnostic — the *caller*
        // (OrderIntentService.ComputeCheckoutBreakdown) picks whether to
        // pass the package price or the variant price. This test
        // documents the invariant: whatever price you pass IS what the
        // pro-rata is computed from. If a variant's Price is R1899 and
        // the package's is R1499, the caller must pass 1899 here.
        var variantMonthly = 1899m;
        var packageMonthly = 1499m;
        var start = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var variantProRata = ProRataCalculator.ComputeProRataAmount(variantMonthly, start, 15);
        var packageProRata = ProRataCalculator.ComputeProRataAmount(packageMonthly, start, 15);

        variantProRata.Should().NotBe(packageProRata,
            "the caller must pass the variant price when a variant is selected");
        variantProRata.Should().Be(696.30m); // 1899 × 11 / 30 = 696.30
        packageProRata.Should().Be(549.63m); // 1499 × 11 / 30 = 549.63
    }
}

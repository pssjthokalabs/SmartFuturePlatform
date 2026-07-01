namespace SmartFuture.Application.Billing.ProRata;

// Pure, testable pro-rata computation for the customer-selectable
// billing-day feature.
//
// Formula (approved by the business):
//   proRataAmount = round(monthlyPrice × billableDays / 30, 2, AwayFromZero)
//
// Date convention (approved):
//   Pro-rata period = [startDate (inclusive), nextBillingDate (exclusive)].
//   So billableDays = (nextBillingDate - startDate).TotalDays.
//
// Denominator = 30 for stability across months of different lengths
// (Feb 28/29, Apr 30, Jul 31…). Documented in code + invoice descriptions
// so admin/customer can audit any given line.
//
// "Next billing date" rules (see NextBillingDate):
//   - If startDate lands on the effective billing day for its own month
//     (e.g. billingDay=30 on 28 Feb, or billingDay=15 on 15 Jun), the
//     period is empty (nextBillingDate == startDate). Callers should
//     skip generating a pro-rata line item in that case.
//   - Otherwise nextBillingDate is the next chronological occurrence of
//     the preferred billing day, clamped to the last day of the month
//     when the month is shorter than the raw billing day (billingDay=30
//     in Feb resolves to Feb 28 / 29).
//
// All dates are UTC DateOnly-shaped — we compare by year/month/day and
// ignore the time-of-day component. The methods accept DateTime for
// interop with the rest of the codebase; callers should pass .Date.
public static class ProRataCalculator
{
    // Compute the first day of the next monthly billing period for a
    // customer starting on `startDate` with preferred day `billingDay`.
    // See file-level doc for the full ruleset + edge cases.
    public static DateTime NextBillingDate(DateTime startDate, int billingDay)
    {
        if (billingDay < 1 || billingDay > 31)
            throw new ArgumentOutOfRangeException(nameof(billingDay), billingDay, "billingDay must be in 1..31");

        var start = startDate.Date;
        var thisMonthEffective = ResolveEffectiveDay(start.Year, start.Month, billingDay);

        // Customer joins ON their billing day (or on the last day of a
        // short month when they picked 30/31): no pro-rata window.
        if (start.Day == thisMonthEffective)
            return start;

        if (start.Day < thisMonthEffective)
            return new DateTime(start.Year, start.Month, thisMonthEffective, 0, 0, 0, DateTimeKind.Utc);

        // Joined AFTER the billing day for this month: roll into next
        // month, clamped for short months (e.g. 30 → 28 in Feb).
        var next = start.AddMonths(1);
        var nextMonthEffective = ResolveEffectiveDay(next.Year, next.Month, billingDay);
        return new DateTime(next.Year, next.Month, nextMonthEffective, 0, 0, 0, DateTimeKind.Utc);
    }

    // Days in the [startDate, nextBillingDate) window. Zero means "no
    // pro-rata due" (customer joined on their billing day).
    public static int ComputeBillableDays(DateTime startDate, int billingDay)
    {
        var next = NextBillingDate(startDate, billingDay);
        var days = (int)(next.Date - startDate.Date).TotalDays;
        return days < 0 ? 0 : days;
    }

    // Compute the pro-rata amount using the fixed 30-day denominator.
    // Rounded to the currency's minor unit (2 decimals) using
    // AwayFromZero — matches how the invoice line item and gateway
    // amount are stored (decimal(18,2)).
    public static decimal ComputeProRataAmount(decimal monthlyPrice, DateTime startDate, int billingDay)
    {
        if (monthlyPrice <= 0m) return 0m;
        var days = ComputeBillableDays(startDate, billingDay);
        if (days <= 0) return 0m;
        var raw = monthlyPrice * days / 30m;
        return Math.Round(raw, 2, MidpointRounding.AwayFromZero);
    }

    // Bundle the result for callers that need the whole picture (invoice
    // description + amount + audit).
    public record ProRataQuote(
        DateTime StartDateUtc,
        DateTime NextBillingDateUtc,
        int BillableDays,
        decimal MonthlyPrice,
        decimal ProRataAmount);

    public static ProRataQuote Quote(decimal monthlyPrice, DateTime startDate, int billingDay)
    {
        var next = NextBillingDate(startDate, billingDay);
        var days = (int)(next.Date - startDate.Date).TotalDays;
        if (days < 0) days = 0;
        var amount = (monthlyPrice > 0m && days > 0)
            ? Math.Round(monthlyPrice * days / 30m, 2, MidpointRounding.AwayFromZero)
            : 0m;
        return new ProRataQuote(startDate.Date, next, days, monthlyPrice, amount);
    }

    // Format the customer-facing description for a pro-rata line item.
    // Uses the "[start, nextBillingDate)" convention with the last
    // billable day rendered (nextBillingDate.AddDays(-1)) so the customer
    // sees the range they were actually charged for. Falls back to
    // "starting <date>" when the window is degenerate.
    public static string FormatProRataDescription(DateTime startDate, DateTime nextBillingDate)
    {
        var lastDay = nextBillingDate.AddDays(-1);
        if (lastDay.Date < startDate.Date)
            return $"Pro-rata service charge: starting {startDate:d MMM yyyy}";
        return $"Pro-rata service charge: {startDate:d MMM yyyy} – {lastDay:d MMM yyyy}";
    }

    // Description for the first full monthly period after the pro-rata
    // window. Matches the format the customer already saw on their
    // pro-rata line so admin support can trace the sequence.
    public static string FormatMonthlyDescription(DateTime periodStart, DateTime periodEnd)
    {
        var lastDay = periodEnd.AddDays(-1);
        return $"Monthly service charge: {periodStart:d MMM yyyy} – {lastDay:d MMM yyyy}";
    }

    // Resolve the effective day-of-month for a given year/month. Handles
    // the "billing day 30 in February" rule + the general "billing day
    // greater than DaysInMonth" clamp. Not month/year-agnostic (Feb 2028
    // = 29, Feb 2026 = 28).
    private static int ResolveEffectiveDay(int year, int month, int billingDay)
    {
        var maxDay = DateTime.DaysInMonth(year, month);
        return billingDay > maxDay ? maxDay : billingDay;
    }
}

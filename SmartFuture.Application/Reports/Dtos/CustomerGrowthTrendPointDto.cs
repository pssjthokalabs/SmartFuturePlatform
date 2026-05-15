namespace SmartFuture.Application.Reports.Dtos;

/// <summary>
/// Single point on the customer growth area chart. `Count` is the
/// cumulative total of customer profiles at the end of <see cref="Date"/>
/// (UTC), not the new-customers-that-day count. The service computes a
/// running total starting from a baseline of customers created before
/// the filter's `FromUtc` so the chart joins smoothly to historical
/// data.
/// </summary>
public class CustomerGrowthTrendPointDto
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
}

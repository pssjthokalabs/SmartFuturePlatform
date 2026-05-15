namespace SmartFuture.Application.Reports.Dtos;

/// <summary>
/// Single point on the "Installations: Completed vs Pending" chart.
/// `Completed` and `Pending` are counts within the bucket day (UTC)
/// keyed off the installation's <c>CreatedAtUtc</c>:
///   - <see cref="Completed"/>: installations created that day whose
///     current Status is <c>Completed</c>.
///   - <see cref="Pending"/>: installations created that day whose
///     current Status is one of the pending-bucket statuses
///     (PendingScheduling, Scheduled, TechnicianAssigned, EnRoute,
///     OnSite, Rescheduled).
///
/// This is an approximation rather than a true monthly throughput
/// (the schema has no Completed-On timestamp on installations today),
/// but it tracks the same shape the UI's mock data was authored
/// against.
/// </summary>
public class InstallationStatusTrendPointDto
{
    public DateTime Date { get; set; }
    public int Completed { get; set; }
    public int Pending { get; set; }
}

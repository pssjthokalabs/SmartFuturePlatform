namespace SmartFuture.Application.Dashboard.Dtos;

public class AdminInstallationQueueItemDto
{
    public Guid Id { get; set; }
    public string InstallationNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public DateTime? ScheduledForUtc { get; set; }
    public string? ScheduledDate { get; set; }
    public string? TimeSlot { get; set; }
    public string Status { get; set; } = string.Empty;
}

using SmartFuture.Shared.Enums.Installations;

namespace SmartFuture.Application.Installations.Dtos;

public class AdminUpdateInstallationStatusDto
{
    public InstallationStatus Status { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
    public string? AdminNotes { get; set; }
    public string? TechnicianNotes { get; set; }
    public string? CompletionNotes { get; set; }
    public string? FailureReason { get; set; }
    public string? CancellationReason { get; set; }
}

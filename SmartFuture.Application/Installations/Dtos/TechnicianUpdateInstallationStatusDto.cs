using SmartFuture.Shared.Enums.Installations;

namespace SmartFuture.Application.Installations.Dtos;

/// <summary>
/// Body for <c>POST /api/technician/installations/{id}/status</c>.
///
/// When <see cref="Status"/> is <c>Completed</c>, the completion-detail
/// fields below are required by the service-layer guard. For
/// <c>InProgress</c> they're optional. The technician is identified
/// implicitly from the JWT — there's no <c>TechnicianId</c> on the
/// body so a technician cannot impersonate another technician.
/// </summary>
public class TechnicianUpdateInstallationStatusDto
{
    public InstallationStatus Status { get; set; }

    public string? TechnicianNotes { get; set; }

    // ─── Required for Completed ─────────────────────────────────────
    public string? RouterMakeModel { get; set; }
    public string? RouterSerialNumber { get; set; }
    public string? RouterMacAddress { get; set; }
    public string? OntReference { get; set; }
    public string? InstalledLocationNotes { get; set; }
    public string? SpeedTestResult { get; set; }
    public string? CustomerSignOffName { get; set; }
    public string? CompletionNotes { get; set; }

    // ─── For Failed ─────────────────────────────────────────────────
    public string? FailureReason { get; set; }
}

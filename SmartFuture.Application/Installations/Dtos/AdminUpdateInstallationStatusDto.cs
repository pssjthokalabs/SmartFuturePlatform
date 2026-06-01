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

    /// <summary>
    /// Per-action admin opt-in for the "complete installation and
    /// activate service if auto-debit succeeds" flow.
    ///
    /// Nullable so existing callers (older portal builds, technician
    /// flow, status changes that aren't Completed) continue to work
    /// unchanged. Only consulted when <see cref="Status"/> is
    /// <c>Completed</c>.
    ///
    /// Honored only where the deployment's
    /// <c>ServiceActivation:RequireManualOpenserveActivation</c> policy
    /// allows it. When the policy requires manual carrier activation
    /// and this is <c>true</c>, the backend records the admin override
    /// in the audit/log line and proceeds, since the act of submitting
    /// the modal with the box ticked is an explicit acknowledgment.
    /// When this is <c>false</c> (or null) the service follows the
    /// config-driven path through PaymentApplierService:
    /// PendingPayment → PendingActivation (manual policy) or
    /// PendingPayment → Active (auto-activate policy).
    /// </summary>
    public bool? ActivateServiceIfPaymentSucceeds { get; set; }
}

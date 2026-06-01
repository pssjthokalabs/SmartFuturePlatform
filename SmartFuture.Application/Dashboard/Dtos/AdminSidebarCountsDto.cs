namespace SmartFuture.Application.Dashboard.Dtos;

/// <summary>
/// Lightweight ops-counts payload for the admin sidebar badges. Pulled
/// from <c>GET /api/admin/dashboard/sidebar-counts</c> on sidebar mount
/// + a long polling interval. Deliberately tiny — two <c>COUNT</c>
/// aggregations against NetworkAccounts and one against Orders. No row
/// projections, no joins beyond what the count itself needs.
/// </summary>
public class AdminSidebarCountsDto
{
    public AdminSidebarClientServiceCountsDto ClientServices { get; set; } = new();
    public AdminSidebarOrderCountsDto Orders { get; set; } = new();
}

public class AdminSidebarClientServiceCountsDto
{
    /// <summary>NetworkAccount.Status = Active.</summary>
    public int Active { get; set; }

    /// <summary>
    /// NetworkAccount.Status = Pending (any sub-state — Pending
    /// Installation / Pending Payment / Pending Activation per the
    /// order's lifecycle).
    /// </summary>
    public int Pending { get; set; }

    /// <summary>NetworkAccount.Status = Suspended OR Terminated.</summary>
    public int TerminatedOrSuspended { get; set; }
}

public class AdminSidebarOrderCountsDto
{
    /// <summary>
    /// Orders the admin needs to schedule: payment landed but no
    /// installation row exists yet, OR the linked installation is still
    /// PendingScheduling.
    /// </summary>
    public int PendingInstallation { get; set; }

    /// <summary>
    /// Orders with status = PendingPayment (installation completed,
    /// waiting for the first monthly invoice to clear).
    /// </summary>
    public int PendingPayment { get; set; }

    /// <summary>
    /// Orders with status = PendingActivation (paid, waiting for the
    /// admin's manual Openserve activation).
    /// </summary>
    public int PendingActivation { get; set; }
}

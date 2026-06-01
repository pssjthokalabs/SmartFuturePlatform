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
    /// Pending Installation: NetworkAccount.Status = Pending AND
    /// the order is NOT past installation (i.e. install fee paid,
    /// installation row still in-flight or absent).
    /// </summary>
    public int PendingInstallation { get; set; }

    /// <summary>
    /// Pending Payment: NetworkAccount.Status = Pending AND
    /// Order.Status = PendingPayment (installation completed,
    /// monthly service invoice unpaid).
    /// </summary>
    public int PendingPayment { get; set; }

    /// <summary>
    /// Pending Activation: NetworkAccount.Status = Pending AND
    /// Order.Status = PendingActivation (monthly invoice paid,
    /// admin still needs to flip the Openserve activation).
    /// Surfaced as its own pill so the admin can act on it.
    /// </summary>
    public int PendingActivation { get; set; }

    /// <summary>
    /// Legacy aggregate (PendingInstallation + PendingPayment +
    /// PendingActivation). Kept on the DTO so older portal builds that
    /// only read `pending` keep rendering a non-zero pill while a
    /// rolling deploy is in flight; new builds prefer the three
    /// disaggregated fields above.
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

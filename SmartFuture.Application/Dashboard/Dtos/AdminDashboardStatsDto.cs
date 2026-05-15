namespace SmartFuture.Application.Dashboard.Dtos;

public class AdminDashboardStatsDto
{
    public int TotalCustomers { get; set; }
    public int ActiveServices { get; set; }
    public int NewOrdersThisMonth { get; set; }
    public int PendingInstallations { get; set; }
    public decimal OutstandingPayments { get; set; }
    public int CoverageRequests { get; set; }
    public int OpenSupportTickets { get; set; }
    public int FailedPayments { get; set; }
    public int NetworkAlerts { get; set; }
}

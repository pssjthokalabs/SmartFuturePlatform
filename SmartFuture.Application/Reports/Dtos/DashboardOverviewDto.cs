namespace SmartFuture.Application.Reports.Dtos;

public class DashboardOverviewDto
{
    public DateTime AsOfUtc { get; set; }

    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int TotalCustomerProfiles { get; set; }

    public int TotalCoverageRequests { get; set; }
    public int PendingCoverageRequests { get; set; }
    public int AvailableCoverageRequests { get; set; }

    public int TotalOrders { get; set; }
    public int SubmittedOrders { get; set; }
    public int ActiveOrders { get; set; }
    public int CancelledOrders { get; set; }

    public int TotalInstallations { get; set; }
    public int PendingInstallations { get; set; }
    public int CompletedInstallations { get; set; }
    public int FailedInstallations { get; set; }

    public int TotalInvoices { get; set; }
    public int UnpaidInvoices { get; set; }
    public int PaidInvoices { get; set; }
    public decimal TotalInvoiceAmount { get; set; }
    public decimal TotalPaidAmount { get; set; }
    public decimal OutstandingBalance { get; set; }

    public int TotalPayments { get; set; }
    public int CompletedPayments { get; set; }
    public decimal CompletedPaymentAmount { get; set; }

    public int OpenSupportTickets { get; set; }
    public int CriticalSupportTickets { get; set; }

    public int FailedNotifications { get; set; }
    public int PendingWebhooks { get; set; }
    public int FailedWebhooks { get; set; }
}

namespace SmartFuture.Application.Reports.Dtos;

public class OperationalQueueDto
{
    public int CoverageRequestsInReview { get; set; }
    public int OrdersAwaitingConfirmation { get; set; }
    public int OrdersAwaitingPayment { get; set; }
    public int InstallationsPendingScheduling { get; set; }
    public int InstallationsScheduledToday { get; set; }
    public int OverdueInvoices { get; set; }
    public int FailedPayments { get; set; }
    public int OpenSupportTickets { get; set; }
    public int CriticalSupportTickets { get; set; }
    public int FailedNotifications { get; set; }
    public int FailedWebhooks { get; set; }
}

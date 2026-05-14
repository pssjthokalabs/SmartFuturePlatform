namespace SmartFuture.Application.Admin.Dtos;

public class SystemSummaryDto
{
    public DateTime AsOfUtc { get; set; }

    public int PendingWebhooks { get; set; }
    public int FailedNotifications { get; set; }
    public int OpenSupportTickets { get; set; }
    public int PendingInstallations { get; set; }
    public int UnpaidInvoices { get; set; }
}

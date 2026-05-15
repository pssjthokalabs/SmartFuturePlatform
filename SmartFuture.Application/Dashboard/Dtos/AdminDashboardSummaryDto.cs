namespace SmartFuture.Application.Dashboard.Dtos;

public class AdminDashboardSummaryDto
{
    public DateTime AsOfUtc { get; set; }
    public AdminDashboardStatsDto Stats { get; set; } = new();
    public List<AdminRecentOrderDto> RecentOrders { get; set; } = new();
    public List<AdminInstallationQueueItemDto> InstallationQueue { get; set; } = new();
    public List<AdminPaymentIssueDto> PaymentIssues { get; set; } = new();
    public List<AdminSupportTicketSummaryDto> RecentTickets { get; set; } = new();
    public List<AdminRecentAuditLogDto> RecentAuditLogs { get; set; } = new();
}

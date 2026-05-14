using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Admin.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.SupportTickets;
using SmartFuture.Shared.Enums.Webhooks;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Admin;

public class AdminSystemService : IAdminSystemService
{
    private static readonly InstallationStatus[] PendingInstallationStatuses =
    {
        InstallationStatus.PendingScheduling,
        InstallationStatus.Scheduled,
        InstallationStatus.TechnicianAssigned,
        InstallationStatus.EnRoute,
        InstallationStatus.OnSite,
        InstallationStatus.Rescheduled
    };

    private static readonly SupportTicketStatus[] OpenSupportTicketStatuses =
    {
        SupportTicketStatus.Open,
        SupportTicketStatus.AwaitingCustomer,
        SupportTicketStatus.AwaitingAgent,
        SupportTicketStatus.InProgress,
        SupportTicketStatus.Reopened
    };

    private static readonly InvoiceStatus[] UnpaidInvoiceStatuses =
    {
        InvoiceStatus.Issued,
        InvoiceStatus.PartiallyPaid,
        InvoiceStatus.Overdue
    };

    private readonly IAppDbContext _dbContext;
    private readonly ILogger<AdminSystemService> _logger;

    public AdminSystemService(IAppDbContext dbContext, ILogger<AdminSystemService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<SystemSummaryDto>> GetSystemSummaryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var pendingWebhooks = await _dbContext.WebhookInboxes
                .AsNoTracking()
                .CountAsync(w => w.Status == WebhookInboxStatus.Received, cancellationToken);

            var failedNotifications = await _dbContext.OutboundNotifications
                .AsNoTracking()
                .CountAsync(n => n.Status == NotificationStatus.Failed, cancellationToken);

            var openTickets = await _dbContext.SupportTickets
                .AsNoTracking()
                .CountAsync(t => OpenSupportTicketStatuses.Contains(t.Status), cancellationToken);

            var pendingInstallations = await _dbContext.Installations
                .AsNoTracking()
                .CountAsync(i => PendingInstallationStatuses.Contains(i.Status), cancellationToken);

            var unpaidInvoices = await _dbContext.Invoices
                .AsNoTracking()
                .CountAsync(i => UnpaidInvoiceStatuses.Contains(i.Status) && i.BalanceDue > 0, cancellationToken);

            return Result<SystemSummaryDto>.Success(new SystemSummaryDto
            {
                AsOfUtc = DateTime.UtcNow,
                PendingWebhooks = pendingWebhooks,
                FailedNotifications = failedNotifications,
                OpenSupportTickets = openTickets,
                PendingInstallations = pendingInstallations,
                UnpaidInvoices = unpaidInvoices
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building admin system summary");
            return Result<SystemSummaryDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the system summary.");
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Common;
using SmartFuture.Application.Dashboard.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.CoverageRequests;
using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.SupportTickets;
using SmartFuture.Shared.Enums.Webhooks;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Dashboard;

public class AdminDashboardService : IAdminDashboardService
{
    private const int RecentOrdersTake = 5;
    private const int InstallationQueueTake = 4;
    private const int RecentTicketsTake = 4;
    private const int PaymentIssuesTake = 4;
    private const int RecentAuditLogsTake = 5;

    private const string SummaryCacheKey = "dashboard:summary";
    private static readonly TimeSpan SummaryCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly InstallationStatus[] PendingInstallationStatuses =
    {
        InstallationStatus.PendingScheduling, InstallationStatus.Scheduled, InstallationStatus.TechnicianAssigned,
        InstallationStatus.EnRoute, InstallationStatus.OnSite, InstallationStatus.Rescheduled
    };

    private static readonly InstallationStatus[] InstallationQueueStatuses =
    {
        InstallationStatus.Scheduled, InstallationStatus.TechnicianAssigned, InstallationStatus.EnRoute,
        InstallationStatus.OnSite, InstallationStatus.Rescheduled, InstallationStatus.PendingScheduling
    };

    private static readonly SupportTicketStatus[] OpenSupportTicketStatuses =
    {
        SupportTicketStatus.Open, SupportTicketStatus.AwaitingCustomer, SupportTicketStatus.AwaitingAgent,
        SupportTicketStatus.InProgress, SupportTicketStatus.Reopened
    };

    private static readonly InvoiceStatus[] UnpaidInvoiceStatuses =
    {
        InvoiceStatus.Issued, InvoiceStatus.PartiallyPaid, InvoiceStatus.Overdue
    };

    private static readonly CoverageRequestStatus[] PendingCoverageStatuses =
    {
        CoverageRequestStatus.Submitted, CoverageRequestStatus.InReview, CoverageRequestStatus.MoreInfoRequired
    };

    private static readonly DebitOrderMandateStatus[] DebitOrderProblemStatuses =
    {
        DebitOrderMandateStatus.Failed, DebitOrderMandateStatus.Suspended
    };

    private readonly IAppDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdminDashboardService> _logger;

    public AdminDashboardService(IAppDbContext dbContext, IMemoryCache cache, ILogger<AdminDashboardService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _logger = logger;
    }

    public async Task<Result<AdminDashboardSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_cache.TryGetValue(SummaryCacheKey, out AdminDashboardSummaryDto? cached) && cached is not null)
                return Result<AdminDashboardSummaryDto>.Success(cached);

            var dto = new AdminDashboardSummaryDto { AsOfUtc = DateTime.UtcNow };
            dto.Stats = await BuildStatsAsync(cancellationToken);
            dto.RecentOrders = await BuildRecentOrdersAsync(cancellationToken);
            dto.InstallationQueue = await BuildInstallationQueueAsync(cancellationToken);
            dto.PaymentIssues = await BuildPaymentIssuesAsync(cancellationToken);
            dto.RecentTickets = await BuildRecentTicketsAsync(cancellationToken);
            dto.RecentAuditLogs = await BuildRecentAuditLogsAsync(cancellationToken);

            _cache.Set(SummaryCacheKey, dto, SummaryCacheTtl);
            return Result<AdminDashboardSummaryDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building admin dashboard summary");
            return Result<AdminDashboardSummaryDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the dashboard summary.");
        }
    }

    private async Task<AdminDashboardStatsDto> BuildStatsAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var monthStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStartUtc = monthStartUtc.AddMonths(1);

        // All business stats EXCLUDE controlled QA test accounts so test
        // activity never inflates real dashboard numbers (see TestAccountFilters).
        var totalCustomers = await _dbContext.CustomerProfiles.AsNoTracking()
            .ExcludeTestAccounts().CountAsync(cancellationToken);

        var activeServices = await _dbContext.Orders.AsNoTracking().ExcludeTestAccounts()
            .CountAsync(o => o.Status == OrderStatus.Active, cancellationToken);

        var newOrdersThisMonth = await _dbContext.Orders.AsNoTracking().ExcludeTestAccounts()
            .CountAsync(o => o.CreatedAtUtc >= monthStartUtc && o.CreatedAtUtc < nextMonthStartUtc, cancellationToken);

        var pendingInstallations = await _dbContext.Installations.AsNoTracking().ExcludeTestAccounts()
            .CountAsync(i => PendingInstallationStatuses.Contains(i.Status), cancellationToken);

        var outstandingPayments = await _dbContext.Invoices.AsNoTracking().ExcludeTestAccounts()
            .Where(i => UnpaidInvoiceStatuses.Contains(i.Status) && i.BalanceDue > 0)
            .SumAsync(i => (decimal?)i.BalanceDue, cancellationToken) ?? 0m;

        var coverageRequests = await _dbContext.CoverageRequests.AsNoTracking()
            .CountAsync(c => PendingCoverageStatuses.Contains(c.Status), cancellationToken);

        var openSupportTickets = await _dbContext.SupportTickets.AsNoTracking().ExcludeTestAccounts()
            .CountAsync(t => OpenSupportTicketStatuses.Contains(t.Status), cancellationToken);

        var failedPayments = await _dbContext.Payments.AsNoTracking().ExcludeTestAccounts()
            .CountAsync(p => p.Status == PaymentStatus.Failed, cancellationToken);

        // NetworkAlerts is a proxy: the SmartFuture backend has no dedicated
        // "network alert" concept yet, so we use failed notifications +
        // failed/signature-invalid webhooks as a stand-in for "things ops
        // probably needs to know about right now". The frontend label
        // ("Active incidents") fits either reading.
        var failedNotifications = await _dbContext.OutboundNotifications.AsNoTracking()
            .CountAsync(n => n.Status == NotificationStatus.Failed, cancellationToken);

        var failedWebhooks = await _dbContext.WebhookInboxes.AsNoTracking()
            .CountAsync(w => w.Status == WebhookInboxStatus.Failed || w.Status == WebhookInboxStatus.SignatureInvalid, cancellationToken);

        return new AdminDashboardStatsDto
        {
            TotalCustomers = totalCustomers,
            ActiveServices = activeServices,
            NewOrdersThisMonth = newOrdersThisMonth,
            PendingInstallations = pendingInstallations,
            OutstandingPayments = outstandingPayments,
            CoverageRequests = coverageRequests,
            OpenSupportTickets = openSupportTickets,
            FailedPayments = failedPayments,
            NetworkAlerts = failedNotifications + failedWebhooks
        };
    }

    private async Task<List<AdminRecentOrderDto>> BuildRecentOrdersAsync(CancellationToken cancellationToken)
    {
        // The Order entity has no explicit "type" (new connection / upgrade /
        // migration). We project Order.Source as OrderType so the column is
        // non-empty; the UI just renders whatever string we provide.
        return await _dbContext.Orders.AsNoTracking().ExcludeTestAccounts()
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(RecentOrdersTake)
            .Select(o => new AdminRecentOrderDto
            {
                Id = o.Id,
                OrderNumber = o.OrderNumber,
                CustomerName = o.User != null
                    ? (o.User.FirstName + " " + o.User.LastName).Trim()
                    : (o.FullName ?? string.Empty),
                PackageName = o.PackageName,
                OrderType = o.Source.ToString(),
                Status = o.Status.ToString(),
                Amount = o.PackagePrice,
                CreatedAtUtc = o.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<List<AdminInstallationQueueItemDto>> BuildInstallationQueueAsync(CancellationToken cancellationToken)
    {
        // Order by ScheduledForUtc ascending (nulls last) so the next
        // upcoming installation is at the top. EF Core's null-ordering is
        // provider-specific; we sort client-side after fetching a small
        // candidate set to keep the behavior deterministic.
        var candidates = await _dbContext.Installations.AsNoTracking().ExcludeTestAccounts()
            .Where(i => InstallationQueueStatuses.Contains(i.Status))
            .OrderBy(i => i.ScheduledForUtc == null ? 1 : 0)
            .ThenBy(i => i.ScheduledForUtc)
            .Take(InstallationQueueTake)
            .Select(i => new
            {
                i.Id,
                i.InstallationNumber,
                CustomerName = i.Order != null && i.Order.User != null
                    ? (i.Order.User.FirstName + " " + i.Order.User.LastName).Trim()
                    : (i.Order != null ? (i.Order.FullName ?? string.Empty) : string.Empty),
                PackageName = i.Order != null ? i.Order.PackageName : string.Empty,
                i.ScheduledForUtc,
                Status = i.Status.ToString()
            })
            .ToListAsync(cancellationToken);

        return candidates
            .Select(c => new AdminInstallationQueueItemDto
            {
                Id = c.Id,
                InstallationNumber = c.InstallationNumber,
                CustomerName = c.CustomerName,
                PackageName = c.PackageName,
                ScheduledForUtc = c.ScheduledForUtc,
                ScheduledDate = c.ScheduledForUtc?.ToString("yyyy-MM-dd"),
                TimeSlot = c.ScheduledForUtc?.ToString("HH:mm") + (c.ScheduledForUtc.HasValue ? " UTC" : null),
                Status = c.Status
            })
            .ToList();
    }

    private async Task<List<AdminPaymentIssueDto>> BuildPaymentIssuesAsync(CancellationToken cancellationToken)
    {
        // Sourced from DebitOrderMandate (Failed/Suspended) to match the
        // dashboard widget's "Payment / Debit Order" shape. The Payment
        // entity's failure-only counter feeds the "Failed Payments" stat
        // card separately.
        return await _dbContext.DebitOrderMandates.AsNoTracking()
            .Where(d => DebitOrderProblemStatuses.Contains(d.Status))
            .OrderByDescending(d => d.UpdatedAtUtc ?? d.CreatedAtUtc)
            .Take(PaymentIssuesTake)
            .Select(d => new AdminPaymentIssueDto
            {
                Id = d.Id,
                CustomerName = d.User != null
                    ? (d.User.FirstName + " " + d.User.LastName).Trim()
                    : (d.AccountHolderName ?? string.Empty),
                Bank = d.BankName,
                LastResult = d.Notes ?? d.AdminNotes,
                Amount = d.Amount ?? 0m,
                Status = d.Status.ToString()
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<List<AdminSupportTicketSummaryDto>> BuildRecentTicketsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.SupportTickets.AsNoTracking().ExcludeTestAccounts()
            .Where(t => OpenSupportTicketStatuses.Contains(t.Status))
            .OrderByDescending(t => t.UpdatedAtUtc ?? t.CreatedAtUtc)
            .Take(RecentTicketsTake)
            .Select(t => new AdminSupportTicketSummaryDto
            {
                Id = t.Id,
                TicketNumber = t.TicketNumber,
                CustomerName = t.User != null
                    ? (t.User.FirstName + " " + t.User.LastName).Trim()
                    : string.Empty,
                Subject = t.Subject,
                Category = t.Category.ToString(),
                Priority = t.Priority.ToString(),
                Status = t.Status.ToString()
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<List<AdminRecentAuditLogDto>> BuildRecentAuditLogsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.AuditLogs.AsNoTracking()
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(RecentAuditLogsTake)
            .Select(a => new AdminRecentAuditLogDto
            {
                Id = a.Id,
                AdminUser = a.ActorUser != null
                    ? ((a.ActorUser.FirstName + " " + a.ActorUser.LastName).Trim() == string.Empty
                        ? a.ActorUser.Email
                        : (a.ActorUser.FirstName + " " + a.ActorUser.LastName).Trim())
                    : a.ActorType.ToString(),
                Action = a.ActionType.ToString(),
                Entity = a.EntityType.ToString(),
                EntityId = a.EntityId,
                EntityName = a.EntityName,
                Details = a.Summary,
                Timestamp = a.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);
    }

    // ───────────────────────────────────────────────────────────────
    // Sidebar count pills (go-live operational badges)
    // ───────────────────────────────────────────────────────────────
    //
    // Three Client Services buckets and three Orders buckets. Each is
    // a single COUNT — no projections, no joins beyond the EF row
    // filter — so polling this every 60–120s from the sidebar is
    // negligible load even at thousands of rows. 30-second IMemoryCache
    // matches GetSummary's caching pattern so a navigation burst
    // (admin clicks 4 sidebar items in 10s) hits the DB exactly once.

    private const string SidebarCountsCacheKey = "dashboard:sidebar-counts";
    private static readonly TimeSpan SidebarCountsCacheTtl = TimeSpan.FromSeconds(30);

    public async Task<Result<AdminSidebarCountsDto>> GetSidebarCountsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_cache.TryGetValue(SidebarCountsCacheKey, out AdminSidebarCountsDto? cached) && cached is not null)
                return Result<AdminSidebarCountsDto>.Success(cached);

            var active = await _dbContext.NetworkAccounts.ExcludeTestAccounts()
                .CountAsync(n => n.Status == NetworkAccountStatus.Active, cancellationToken);
            var pending = await _dbContext.NetworkAccounts.ExcludeTestAccounts()
                .CountAsync(n => n.Status == NetworkAccountStatus.Pending, cancellationToken);
            // Money-safety brief #5 — disaggregate the legacy `pending`
            // pill into Pending Installation / Pending Payment / Pending
            // Activation so admins can act on each sub-state directly.
            // Pending Activation specifically is the "customer has paid,
            // please go flip the Openserve switch" queue.
            var pendingPaymentSvc = await _dbContext.NetworkAccounts.ExcludeTestAccounts()
                .CountAsync(n => n.Status == NetworkAccountStatus.Pending
                              && n.Order!.Status == OrderStatus.PendingPayment,
                    cancellationToken);
            var pendingActivationSvc = await _dbContext.NetworkAccounts.ExcludeTestAccounts()
                .CountAsync(n => n.Status == NetworkAccountStatus.Pending
                              && n.Order!.Status == OrderStatus.PendingActivation,
                    cancellationToken);
            // Everything else under Pending = "still on installation
            // side of the lifecycle".
            var pendingInstallationSvc = pending - pendingPaymentSvc - pendingActivationSvc;
            if (pendingInstallationSvc < 0) pendingInstallationSvc = 0;
            var terminatedOrSuspended = await _dbContext.NetworkAccounts.ExcludeTestAccounts()
                .CountAsync(n =>
                    n.Status == NetworkAccountStatus.Suspended ||
                    n.Status == NetworkAccountStatus.Terminated ||
                    n.Status == NetworkAccountStatus.Failed,
                    cancellationToken);

            // Orders the admin still has to schedule: a Pending NetworkAccount
            // exists for the order (installation fee paid OR install
            // completed and waiting on monthly invoice) but no installation
            // row is on a "completed/cancelled/failed" terminal status.
            // Cheap approximation: count orders whose backing NetworkAccount
            // is Pending AND no terminal Installation row exists.
            var pendingInstallation = await _dbContext.Orders.ExcludeTestAccounts()
                .CountAsync(o =>
                    (o.Status == OrderStatus.PaymentReceived
                     || o.Status == OrderStatus.Confirmed
                     || o.Status == OrderStatus.Provisioning)
                    && !_dbContext.Installations.Any(i =>
                        i.OrderId == o.Id &&
                        (i.Status == InstallationStatus.Completed ||
                         i.Status == InstallationStatus.Cancelled ||
                         i.Status == InstallationStatus.Failed)),
                    cancellationToken);
            var pendingPayment = await _dbContext.Orders.ExcludeTestAccounts()
                .CountAsync(o => o.Status == OrderStatus.PendingPayment, cancellationToken);
            var pendingActivation = await _dbContext.Orders.ExcludeTestAccounts()
                .CountAsync(o => o.Status == OrderStatus.PendingActivation, cancellationToken);

            var dto = new AdminSidebarCountsDto
            {
                ClientServices = new AdminSidebarClientServiceCountsDto
                {
                    Active                = active,
                    Pending               = pending,
                    PendingInstallation   = pendingInstallationSvc,
                    PendingPayment        = pendingPaymentSvc,
                    PendingActivation     = pendingActivationSvc,
                    TerminatedOrSuspended = terminatedOrSuspended,
                },
                Orders = new AdminSidebarOrderCountsDto
                {
                    PendingInstallation = pendingInstallation,
                    PendingPayment      = pendingPayment,
                    PendingActivation   = pendingActivation,
                },
            };

            _cache.Set(SidebarCountsCacheKey, dto, SidebarCountsCacheTtl);
            return Result<AdminSidebarCountsDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute admin sidebar counts");
            return Result<AdminSidebarCountsDto>.Failure(
                ErrorCodes.EXCEPTION, "Failed to compute admin sidebar counts.");
        }
    }
}

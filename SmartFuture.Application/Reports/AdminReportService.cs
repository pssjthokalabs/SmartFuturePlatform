using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.Reports.Dtos;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.CoverageRequests;
using SmartFuture.Domain.Installations;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.CoverageRequests;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Enums.SupportTickets;
using SmartFuture.Shared.Enums.Webhooks;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Reports;

public class AdminReportService : IAdminReportService
{
    private const int DefaultTrendDays = 30;
    private static readonly TimeSpan OverviewCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationalQueueCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PackageRegionalCacheTtl = TimeSpan.FromSeconds(60);

    private static readonly OrderStatus[] ActiveOrderStatuses =
    {
        OrderStatus.Submitted,
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment,
        OrderStatus.PaymentReceived,
        OrderStatus.Provisioning,
        OrderStatus.Active
    };

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
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdminReportService> _logger;

    public AdminReportService(IAppDbContext dbContext, IMemoryCache cache, ILogger<AdminReportService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _logger = logger;
    }

    public async Task<Result<DashboardOverviewDto>> GetDashboardOverviewAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var cacheKey = $"reports:overview:{CacheKey(filter)}";

            if (_cache.TryGetValue(cacheKey, out DashboardOverviewDto? cached) && cached is not null)
                return Result<DashboardOverviewDto>.Success(cached);

            var result = await BuildDashboardOverviewAsync(cancellationToken);

            _cache.Set(cacheKey, result, OverviewCacheTtl);
            return Result<DashboardOverviewDto>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building dashboard overview");
            return Result<DashboardOverviewDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the dashboard overview.");
        }
    }

    public async Task<Result<OperationalQueueDto>> GetOperationalQueueAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            const string cacheKey = "reports:operational-queue";
            if (_cache.TryGetValue(cacheKey, out OperationalQueueDto? cached) && cached is not null)
                return Result<OperationalQueueDto>.Success(cached);

            var todayStartUtc = DateTime.UtcNow.Date;
            var tomorrowStartUtc = todayStartUtc.AddDays(1);

            var dto = new OperationalQueueDto
            {
                CoverageRequestsInReview = await _dbContext.CoverageRequests.AsNoTracking()
                    .CountAsync(c => c.Status == CoverageRequestStatus.InReview, cancellationToken),

                OrdersAwaitingConfirmation = await _dbContext.Orders.AsNoTracking()
                    .CountAsync(o => o.Status == OrderStatus.Submitted, cancellationToken),

                OrdersAwaitingPayment = await _dbContext.Orders.AsNoTracking()
                    .CountAsync(o => o.Status == OrderStatus.AwaitingPayment, cancellationToken),

                InstallationsPendingScheduling = await _dbContext.Installations.AsNoTracking()
                    .CountAsync(i => i.Status == InstallationStatus.PendingScheduling, cancellationToken),

                InstallationsScheduledToday = await _dbContext.Installations.AsNoTracking()
                    .CountAsync(i => i.ScheduledForUtc != null
                                  && i.ScheduledForUtc >= todayStartUtc
                                  && i.ScheduledForUtc < tomorrowStartUtc, cancellationToken),

                OverdueInvoices = await _dbContext.Invoices.AsNoTracking()
                    .CountAsync(i => i.Status == InvoiceStatus.Overdue
                                  || (UnpaidInvoiceStatuses.Contains(i.Status)
                                      && i.DueAtUtc != null && i.DueAtUtc < todayStartUtc
                                      && i.BalanceDue > 0), cancellationToken),

                FailedPayments = await _dbContext.Payments.AsNoTracking()
                    .CountAsync(p => p.Status == PaymentStatus.Failed, cancellationToken),

                OpenSupportTickets = await _dbContext.SupportTickets.AsNoTracking()
                    .CountAsync(t => OpenSupportTicketStatuses.Contains(t.Status), cancellationToken),

                CriticalSupportTickets = await _dbContext.SupportTickets.AsNoTracking()
                    .CountAsync(t => OpenSupportTicketStatuses.Contains(t.Status)
                                  && t.Priority == SupportTicketPriority.Critical, cancellationToken),

                FailedNotifications = await _dbContext.OutboundNotifications.AsNoTracking()
                    .CountAsync(n => n.Status == NotificationStatus.Failed, cancellationToken),

                FailedWebhooks = await _dbContext.WebhookInboxes.AsNoTracking()
                    .CountAsync(w => w.Status == WebhookInboxStatus.Failed
                                  || w.Status == WebhookInboxStatus.SignatureInvalid, cancellationToken)
            };

            _cache.Set(cacheKey, dto, OperationalQueueCacheTtl);
            return Result<OperationalQueueDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building operational queue");
            return Result<OperationalQueueDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the operational queue.");
        }
    }

    public async Task<Result<List<CountTrendPointDto>>> GetOrdersTrendAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            var query = _dbContext.Orders.AsNoTracking()
                .Where(o => o.CreatedAtUtc >= from && o.CreatedAtUtc < to);

            query = ApplyOrderFilter(query, filter);

            var grouped = await query
                .GroupBy(o => new { o.CreatedAtUtc.Year, o.CreatedAtUtc.Month, o.CreatedAtUtc.Day })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    Count = g.Count()
                })
                .ToListAsync(cancellationToken);

            var points = grouped
                .Select(g => new CountTrendPointDto
                {
                    Date = new DateTime(g.Year, g.Month, g.Day, 0, 0, 0, DateTimeKind.Utc),
                    Count = g.Count
                })
                .OrderBy(p => p.Date)
                .ToList();

            return Result<List<CountTrendPointDto>>.Success(points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building orders trend");
            return Result<List<CountTrendPointDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the orders trend.");
        }
    }

    public async Task<Result<List<CountTrendPointDto>>> GetCoverageRequestsTrendAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            var query = _dbContext.CoverageRequests.AsNoTracking()
                .Where(c => c.CreatedAtUtc >= from && c.CreatedAtUtc < to);

            query = ApplyCoverageRequestFilter(query, filter);

            var grouped = await query
                .GroupBy(c => new { c.CreatedAtUtc.Year, c.CreatedAtUtc.Month, c.CreatedAtUtc.Day })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    Count = g.Count()
                })
                .ToListAsync(cancellationToken);

            var points = grouped
                .Select(g => new CountTrendPointDto
                {
                    Date = new DateTime(g.Year, g.Month, g.Day, 0, 0, 0, DateTimeKind.Utc),
                    Count = g.Count
                })
                .OrderBy(p => p.Date)
                .ToList();

            return Result<List<CountTrendPointDto>>.Success(points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building coverage requests trend");
            return Result<List<CountTrendPointDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the coverage requests trend.");
        }
    }

    public async Task<Result<List<RevenueTrendPointDto>>> GetInvoiceRevenueTrendAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            var query = _dbContext.Invoices.AsNoTracking()
                .Where(i => i.IssuedAtUtc != null
                         && i.IssuedAtUtc >= from
                         && i.IssuedAtUtc < to);

            query = ApplyInvoiceFilter(query, filter);

            var grouped = await query
                .GroupBy(i => new { i.IssuedAtUtc!.Value.Year, i.IssuedAtUtc!.Value.Month, i.IssuedAtUtc!.Value.Day })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    Amount = g.Sum(i => i.TotalAmount)
                })
                .ToListAsync(cancellationToken);

            var points = grouped
                .Select(g => new RevenueTrendPointDto
                {
                    Date = new DateTime(g.Year, g.Month, g.Day, 0, 0, 0, DateTimeKind.Utc),
                    Amount = g.Amount
                })
                .OrderBy(p => p.Date)
                .ToList();

            return Result<List<RevenueTrendPointDto>>.Success(points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building invoice revenue trend");
            return Result<List<RevenueTrendPointDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the invoice revenue trend.");
        }
    }

    public async Task<Result<List<RevenueTrendPointDto>>> GetPaymentRevenueTrendAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            var query = _dbContext.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Completed
                         && p.PaidAtUtc != null
                         && p.PaidAtUtc >= from
                         && p.PaidAtUtc < to);

            query = ApplyPaymentFilter(query, filter);

            var grouped = await query
                .GroupBy(p => new { p.PaidAtUtc!.Value.Year, p.PaidAtUtc!.Value.Month, p.PaidAtUtc!.Value.Day })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    Amount = g.Sum(p => p.Amount)
                })
                .ToListAsync(cancellationToken);

            var points = grouped
                .Select(g => new RevenueTrendPointDto
                {
                    Date = new DateTime(g.Year, g.Month, g.Day, 0, 0, 0, DateTimeKind.Utc),
                    Amount = g.Amount
                })
                .OrderBy(p => p.Date)
                .ToList();

            return Result<List<RevenueTrendPointDto>>.Success(points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building payment revenue trend");
            return Result<List<RevenueTrendPointDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the payment revenue trend.");
        }
    }

    public async Task<Result<List<StatusBreakdownDto>>> GetOrderStatusBreakdownAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            var query = _dbContext.Orders.AsNoTracking()
                .Where(o => o.CreatedAtUtc >= from && o.CreatedAtUtc < to);

            query = ApplyOrderFilter(query, filter);

            var grouped = await query
                .GroupBy(o => o.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var result = grouped
                .Select(g => new StatusBreakdownDto { Label = g.Status.ToString(), Count = g.Count })
                .OrderByDescending(b => b.Count)
                .ToList();

            return Result<List<StatusBreakdownDto>>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building order status breakdown");
            return Result<List<StatusBreakdownDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the order status breakdown.");
        }
    }

    public async Task<Result<List<StatusBreakdownDto>>> GetInstallationStatusBreakdownAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            var query = _dbContext.Installations.AsNoTracking()
                .Where(i => i.CreatedAtUtc >= from && i.CreatedAtUtc < to);

            query = ApplyInstallationFilter(query, filter);

            var grouped = await query
                .GroupBy(i => i.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var result = grouped
                .Select(g => new StatusBreakdownDto { Label = g.Status.ToString(), Count = g.Count })
                .OrderByDescending(b => b.Count)
                .ToList();

            return Result<List<StatusBreakdownDto>>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building installation status breakdown");
            return Result<List<StatusBreakdownDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the installation status breakdown.");
        }
    }

    public async Task<Result<List<StatusBreakdownDto>>> GetSupportTicketStatusBreakdownAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            var query = _dbContext.SupportTickets.AsNoTracking()
                .Where(t => t.CreatedAtUtc >= from && t.CreatedAtUtc < to);

            var grouped = await query
                .GroupBy(t => t.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var result = grouped
                .Select(g => new StatusBreakdownDto { Label = g.Status.ToString(), Count = g.Count })
                .OrderByDescending(b => b.Count)
                .ToList();

            return Result<List<StatusBreakdownDto>>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building support ticket status breakdown");
            return Result<List<StatusBreakdownDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the support ticket status breakdown.");
        }
    }

    public async Task<Result<List<ServicePackagePerformanceDto>>> GetServicePackagePerformanceAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var cacheKey = $"reports:package-performance:{CacheKey(filter)}";
            if (_cache.TryGetValue(cacheKey, out List<ServicePackagePerformanceDto>? cached) && cached is not null)
                return Result<List<ServicePackagePerformanceDto>>.Success(cached);

            var (from, to) = EffectiveRange(filter);

            var orderQuery = _dbContext.Orders.AsNoTracking()
                .Where(o => o.CreatedAtUtc >= from && o.CreatedAtUtc < to
                         && o.ServicePackageId != null);

            orderQuery = ApplyOrderFilter(orderQuery, filter);

            var orderAgg = await orderQuery
                .GroupBy(o => new { o.ServicePackageId, o.PackageName, o.PackageType })
                .Select(g => new
                {
                    g.Key.ServicePackageId,
                    g.Key.PackageName,
                    g.Key.PackageType,
                    OrderCount = g.Count(),
                    ActiveOrderCount = g.Count(o => ActiveOrderStatuses.Contains(o.Status))
                })
                .ToListAsync(cancellationToken);

            var invoiceAgg = await _dbContext.Invoices.AsNoTracking()
                .Where(i => i.IssuedAtUtc != null
                         && i.IssuedAtUtc >= from && i.IssuedAtUtc < to
                         && i.Order != null && i.Order.ServicePackageId != null)
                .GroupBy(i => i.Order!.ServicePackageId)
                .Select(g => new
                {
                    ServicePackageId = g.Key,
                    TotalInvoiceAmount = g.Sum(i => i.TotalAmount),
                    TotalPaidAmount = g.Sum(i => i.AmountPaid)
                })
                .ToListAsync(cancellationToken);

            var invoiceByPackage = invoiceAgg
                .Where(x => x.ServicePackageId.HasValue)
                .ToDictionary(x => x.ServicePackageId!.Value, x => x);

            var result = orderAgg
                .Where(o => o.ServicePackageId.HasValue)
                .Select(o =>
                {
                    invoiceByPackage.TryGetValue(o.ServicePackageId!.Value, out var inv);
                    return new ServicePackagePerformanceDto
                    {
                        ServicePackageId = o.ServicePackageId!.Value,
                        PackageName = o.PackageName,
                        PackageType = o.PackageType.ToString(),
                        OrderCount = o.OrderCount,
                        ActiveOrderCount = o.ActiveOrderCount,
                        TotalInvoiceAmount = inv?.TotalInvoiceAmount ?? 0m,
                        TotalPaidAmount = inv?.TotalPaidAmount ?? 0m
                    };
                })
                .OrderByDescending(p => p.OrderCount)
                .ToList();

            _cache.Set(cacheKey, result, PackageRegionalCacheTtl);
            return Result<List<ServicePackagePerformanceDto>>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building service package performance");
            return Result<List<ServicePackagePerformanceDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the service package performance report.");
        }
    }

    public async Task<Result<List<RegionalDemandDto>>> GetRegionalDemandAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var cacheKey = $"reports:regional:{CacheKey(filter)}";
            if (_cache.TryGetValue(cacheKey, out List<RegionalDemandDto>? cached) && cached is not null)
                return Result<List<RegionalDemandDto>>.Success(cached);

            var (from, to) = EffectiveRange(filter);

            var coverageQuery = _dbContext.CoverageRequests.AsNoTracking()
                .Where(c => c.CreatedAtUtc >= from && c.CreatedAtUtc < to);
            coverageQuery = ApplyCoverageRequestFilter(coverageQuery, filter);

            var coverageGroups = await coverageQuery
                .GroupBy(c => new { c.Province, c.City, c.Suburb })
                .Select(g => new
                {
                    g.Key.Province,
                    g.Key.City,
                    g.Key.Suburb,
                    Count = g.Count()
                })
                .ToListAsync(cancellationToken);

            var orderQuery = _dbContext.Orders.AsNoTracking()
                .Where(o => o.CreatedAtUtc >= from && o.CreatedAtUtc < to);
            orderQuery = ApplyOrderFilter(orderQuery, filter);

            var orderGroups = await orderQuery
                .GroupBy(o => new { o.Province, o.City, o.Suburb })
                .Select(g => new
                {
                    g.Key.Province,
                    g.Key.City,
                    g.Key.Suburb,
                    Count = g.Count()
                })
                .ToListAsync(cancellationToken);

            var installationQuery = _dbContext.Installations.AsNoTracking()
                .Where(i => i.CreatedAtUtc >= from && i.CreatedAtUtc < to);
            installationQuery = ApplyInstallationFilter(installationQuery, filter);

            var installationGroups = await installationQuery
                .GroupBy(i => new { i.Province, i.City, i.Suburb })
                .Select(g => new
                {
                    g.Key.Province,
                    g.Key.City,
                    g.Key.Suburb,
                    Count = g.Count()
                })
                .ToListAsync(cancellationToken);

            var merged = new Dictionary<string, RegionalDemandDto>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in coverageGroups)
                Upsert(merged, g.Province, g.City, g.Suburb).CoverageRequestCount = g.Count;

            foreach (var g in orderGroups)
                Upsert(merged, g.Province, g.City, g.Suburb).OrderCount = g.Count;

            foreach (var g in installationGroups)
                Upsert(merged, g.Province, g.City, g.Suburb).InstallationCount = g.Count;

            var result = merged.Values
                .OrderByDescending(r => r.CoverageRequestCount + r.OrderCount + r.InstallationCount)
                .ThenBy(r => r.Province)
                .ThenBy(r => r.City)
                .ThenBy(r => r.Suburb)
                .ToList();

            _cache.Set(cacheKey, result, PackageRegionalCacheTtl);
            return Result<List<RegionalDemandDto>>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building regional demand");
            return Result<List<RegionalDemandDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the regional demand report.");
        }
    }

    public async Task<Result<List<CustomerGrowthTrendPointDto>>> GetCustomerGrowthTrendAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            // Baseline: customers created before the filter window. Used as
            // the starting cumulative count so the chart joins smoothly to
            // historical data rather than starting from zero.
            var baseline = await _dbContext.CustomerProfiles.AsNoTracking()
                .CountAsync(c => c.CreatedAtUtc < from, cancellationToken);

            var grouped = await _dbContext.CustomerProfiles.AsNoTracking()
                .Where(c => c.CreatedAtUtc >= from && c.CreatedAtUtc < to)
                .GroupBy(c => new { c.CreatedAtUtc.Year, c.CreatedAtUtc.Month, c.CreatedAtUtc.Day })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    Count = g.Count()
                })
                .ToListAsync(cancellationToken);

            var ordered = grouped
                .OrderBy(g => g.Year).ThenBy(g => g.Month).ThenBy(g => g.Day)
                .ToList();

            var points = new List<CustomerGrowthTrendPointDto>(ordered.Count);
            var running = baseline;
            foreach (var g in ordered)
            {
                running += g.Count;
                points.Add(new CustomerGrowthTrendPointDto
                {
                    Date = new DateTime(g.Year, g.Month, g.Day, 0, 0, 0, DateTimeKind.Utc),
                    Count = running
                });
            }

            return Result<List<CustomerGrowthTrendPointDto>>.Success(points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building customer growth trend");
            return Result<List<CustomerGrowthTrendPointDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the customer growth trend.");
        }
    }

    public async Task<Result<List<InstallationStatusTrendPointDto>>> GetInstallationMonthlyStatusTrendAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            var query = _dbContext.Installations.AsNoTracking()
                .Where(i => i.CreatedAtUtc >= from && i.CreatedAtUtc < to);

            query = ApplyInstallationFilter(query, filter);

            // Group by day-of-CreatedAtUtc, then within each day count rows
            // by current Status bucket (Completed vs Pending bucket). The
            // installation schema has no terminal-state timestamp today, so
            // "completed this day" really means "created this day AND now
            // Completed". Documented in InstallationStatusTrendPointDto.
            var grouped = await query
                .GroupBy(i => new { i.CreatedAtUtc.Year, i.CreatedAtUtc.Month, i.CreatedAtUtc.Day })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    Completed = g.Count(i => i.Status == InstallationStatus.Completed),
                    Pending = g.Count(i => PendingInstallationStatuses.Contains(i.Status))
                })
                .ToListAsync(cancellationToken);

            var points = grouped
                .Select(g => new InstallationStatusTrendPointDto
                {
                    Date = new DateTime(g.Year, g.Month, g.Day, 0, 0, 0, DateTimeKind.Utc),
                    Completed = g.Completed,
                    Pending = g.Pending
                })
                .OrderBy(p => p.Date)
                .ToList();

            return Result<List<InstallationStatusTrendPointDto>>.Success(points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building installation monthly status trend");
            return Result<List<InstallationStatusTrendPointDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the installation monthly status trend.");
        }
    }

    public async Task<Result<List<RevenueTrendPointDto>>> GetOutstandingBalanceTrendAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            // Sum of BalanceDue for invoices issued within the window. The
            // BalanceDue value is point-in-time (it reflects current
            // unpaid amount), so this aggregates "new outstanding amount
            // added per period" rather than a true running cash-position
            // trend. Good-enough for the UI's monthly trend view.
            var query = _dbContext.Invoices.AsNoTracking()
                .Where(i => i.IssuedAtUtc != null
                         && i.IssuedAtUtc >= from
                         && i.IssuedAtUtc < to);

            query = ApplyInvoiceFilter(query, filter);

            var grouped = await query
                .GroupBy(i => new { i.IssuedAtUtc!.Value.Year, i.IssuedAtUtc!.Value.Month, i.IssuedAtUtc!.Value.Day })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    Amount = g.Sum(i => i.BalanceDue)
                })
                .ToListAsync(cancellationToken);

            var points = grouped
                .Select(g => new RevenueTrendPointDto
                {
                    Date = new DateTime(g.Year, g.Month, g.Day, 0, 0, 0, DateTimeKind.Utc),
                    Amount = g.Amount
                })
                .OrderBy(p => p.Date)
                .ToList();

            return Result<List<RevenueTrendPointDto>>.Success(points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building outstanding balance trend");
            return Result<List<RevenueTrendPointDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the outstanding balance trend.");
        }
    }

    public async Task<Result<List<CountTrendPointDto>>> GetFailedPaymentsTrendAsync(AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminDashboardFilterRequestDto();
            var (from, to) = EffectiveRange(filter);

            // Failed payments don't have a PaidAtUtc (the payment never
            // settled), so we bucket on CreatedAtUtc — i.e. when the
            // attempt was logged.
            var query = _dbContext.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Failed
                         && p.CreatedAtUtc >= from
                         && p.CreatedAtUtc < to);

            query = ApplyPaymentFilter(query, filter);

            var grouped = await query
                .GroupBy(p => new { p.CreatedAtUtc.Year, p.CreatedAtUtc.Month, p.CreatedAtUtc.Day })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    Count = g.Count()
                })
                .ToListAsync(cancellationToken);

            var points = grouped
                .Select(g => new CountTrendPointDto
                {
                    Date = new DateTime(g.Year, g.Month, g.Day, 0, 0, 0, DateTimeKind.Utc),
                    Count = g.Count
                })
                .OrderBy(p => p.Date)
                .ToList();

            return Result<List<CountTrendPointDto>>.Success(points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building failed payments trend");
            return Result<List<CountTrendPointDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building the failed payments trend.");
        }
    }

    private async Task<DashboardOverviewDto> BuildDashboardOverviewAsync(CancellationToken cancellationToken)
    {
        var dto = new DashboardOverviewDto { AsOfUtc = DateTime.UtcNow };

        dto.TotalUsers = await _dbContext.Users.AsNoTracking().CountAsync(cancellationToken);
        dto.ActiveUsers = await _dbContext.Users.AsNoTracking()
            .CountAsync(u => u.IsActive && u.AccountStatus == UserAccountStatus.Active, cancellationToken);
        dto.TotalCustomerProfiles = await _dbContext.CustomerProfiles.AsNoTracking().CountAsync(cancellationToken);

        dto.TotalCoverageRequests = await _dbContext.CoverageRequests.AsNoTracking().CountAsync(cancellationToken);
        dto.PendingCoverageRequests = await _dbContext.CoverageRequests.AsNoTracking()
            .CountAsync(c => c.Status == CoverageRequestStatus.Submitted
                          || c.Status == CoverageRequestStatus.InReview
                          || c.Status == CoverageRequestStatus.MoreInfoRequired, cancellationToken);
        dto.AvailableCoverageRequests = await _dbContext.CoverageRequests.AsNoTracking()
            .CountAsync(c => c.Status == CoverageRequestStatus.Available, cancellationToken);

        dto.TotalOrders = await _dbContext.Orders.AsNoTracking().CountAsync(cancellationToken);
        dto.SubmittedOrders = await _dbContext.Orders.AsNoTracking()
            .CountAsync(o => o.Status == OrderStatus.Submitted, cancellationToken);
        dto.ActiveOrders = await _dbContext.Orders.AsNoTracking()
            .CountAsync(o => o.Status == OrderStatus.Active, cancellationToken);
        dto.CancelledOrders = await _dbContext.Orders.AsNoTracking()
            .CountAsync(o => o.Status == OrderStatus.Cancelled, cancellationToken);

        dto.TotalInstallations = await _dbContext.Installations.AsNoTracking().CountAsync(cancellationToken);
        dto.PendingInstallations = await _dbContext.Installations.AsNoTracking()
            .CountAsync(i => PendingInstallationStatuses.Contains(i.Status), cancellationToken);
        dto.CompletedInstallations = await _dbContext.Installations.AsNoTracking()
            .CountAsync(i => i.Status == InstallationStatus.Completed, cancellationToken);
        dto.FailedInstallations = await _dbContext.Installations.AsNoTracking()
            .CountAsync(i => i.Status == InstallationStatus.Failed, cancellationToken);

        dto.TotalInvoices = await _dbContext.Invoices.AsNoTracking().CountAsync(cancellationToken);
        dto.UnpaidInvoices = await _dbContext.Invoices.AsNoTracking()
            .CountAsync(i => UnpaidInvoiceStatuses.Contains(i.Status) && i.BalanceDue > 0, cancellationToken);
        dto.PaidInvoices = await _dbContext.Invoices.AsNoTracking()
            .CountAsync(i => i.Status == InvoiceStatus.Paid, cancellationToken);
        dto.TotalInvoiceAmount = await _dbContext.Invoices.AsNoTracking()
            .SumAsync(i => (decimal?)i.TotalAmount, cancellationToken) ?? 0m;
        dto.TotalPaidAmount = await _dbContext.Invoices.AsNoTracking()
            .SumAsync(i => (decimal?)i.AmountPaid, cancellationToken) ?? 0m;
        dto.OutstandingBalance = await _dbContext.Invoices.AsNoTracking()
            .SumAsync(i => (decimal?)i.BalanceDue, cancellationToken) ?? 0m;

        dto.TotalPayments = await _dbContext.Payments.AsNoTracking().CountAsync(cancellationToken);
        dto.CompletedPayments = await _dbContext.Payments.AsNoTracking()
            .CountAsync(p => p.Status == PaymentStatus.Completed, cancellationToken);
        dto.CompletedPaymentAmount = await _dbContext.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Completed)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        dto.OpenSupportTickets = await _dbContext.SupportTickets.AsNoTracking()
            .CountAsync(t => OpenSupportTicketStatuses.Contains(t.Status), cancellationToken);
        dto.CriticalSupportTickets = await _dbContext.SupportTickets.AsNoTracking()
            .CountAsync(t => OpenSupportTicketStatuses.Contains(t.Status)
                          && t.Priority == SupportTicketPriority.Critical, cancellationToken);

        dto.FailedNotifications = await _dbContext.OutboundNotifications.AsNoTracking()
            .CountAsync(n => n.Status == NotificationStatus.Failed, cancellationToken);

        dto.PendingWebhooks = await _dbContext.WebhookInboxes.AsNoTracking()
            .CountAsync(w => w.Status == WebhookInboxStatus.Received, cancellationToken);
        dto.FailedWebhooks = await _dbContext.WebhookInboxes.AsNoTracking()
            .CountAsync(w => w.Status == WebhookInboxStatus.Failed
                          || w.Status == WebhookInboxStatus.SignatureInvalid, cancellationToken);

        return dto;
    }

    private static IQueryable<Order> ApplyOrderFilter(IQueryable<Order> query, AdminDashboardFilterRequestDto filter)
    {
        if (filter.ServicePackageId.HasValue)
            query = query.Where(o => o.ServicePackageId == filter.ServicePackageId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(o => o.Province == v);
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(o => o.City == v);
        }

        return query;
    }

    private static IQueryable<CoverageRequest> ApplyCoverageRequestFilter(IQueryable<CoverageRequest> query, AdminDashboardFilterRequestDto filter)
    {
        if (filter.ServicePackageId.HasValue)
            query = query.Where(c => c.ServicePackageId == filter.ServicePackageId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(c => c.Province == v);
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(c => c.City == v);
        }

        return query;
    }

    private static IQueryable<Installation> ApplyInstallationFilter(IQueryable<Installation> query, AdminDashboardFilterRequestDto filter)
    {
        if (filter.ServicePackageId.HasValue)
            query = query.Where(i => i.Order != null && i.Order.ServicePackageId == filter.ServicePackageId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(i => i.Province == v);
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(i => i.City == v);
        }

        return query;
    }

    private static IQueryable<Invoice> ApplyInvoiceFilter(IQueryable<Invoice> query, AdminDashboardFilterRequestDto filter)
    {
        if (filter.ServicePackageId.HasValue)
            query = query.Where(i => i.Order != null && i.Order.ServicePackageId == filter.ServicePackageId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(i => i.Order != null && i.Order.Province == v);
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(i => i.Order != null && i.Order.City == v);
        }

        return query;
    }

    private static IQueryable<Payment> ApplyPaymentFilter(IQueryable<Payment> query, AdminDashboardFilterRequestDto filter)
    {
        if (filter.ServicePackageId.HasValue)
            query = query.Where(p => p.Invoice != null
                                  && p.Invoice.Order != null
                                  && p.Invoice.Order.ServicePackageId == filter.ServicePackageId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(p => p.Invoice != null
                                  && p.Invoice.Order != null
                                  && p.Invoice.Order.Province == v);
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(p => p.Invoice != null
                                  && p.Invoice.Order != null
                                  && p.Invoice.Order.City == v);
        }

        return query;
    }

    private static (DateTime From, DateTime To) EffectiveRange(AdminDashboardFilterRequestDto filter)
    {
        var to = filter.ToUtc ?? DateTime.UtcNow;
        var from = filter.FromUtc ?? to.AddDays(-DefaultTrendDays);
        if (from > to) (from, to) = (to, from);
        return (from, to);
    }

    private static string CacheKey(AdminDashboardFilterRequestDto filter)
        => $"{filter.FromUtc:o}|{filter.ToUtc:o}|{filter.Province}|{filter.City}|{filter.ServicePackageId}";

    private static RegionalDemandDto Upsert(Dictionary<string, RegionalDemandDto> map, string? province, string? city, string? suburb)
    {
        var key = $"{province?.Trim()}||{city?.Trim()}||{suburb?.Trim()}";
        if (!map.TryGetValue(key, out var entry))
        {
            entry = new RegionalDemandDto
            {
                Province = string.IsNullOrWhiteSpace(province) ? null : province.Trim(),
                City = string.IsNullOrWhiteSpace(city) ? null : city.Trim(),
                Suburb = string.IsNullOrWhiteSpace(suburb) ? null : suburb.Trim()
            };
            map[key] = entry;
        }
        return entry;
    }
}

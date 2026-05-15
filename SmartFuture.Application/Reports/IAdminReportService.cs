using SmartFuture.Application.Reports.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Reports;

public interface IAdminReportService
{
    Task<Result<DashboardOverviewDto>> GetDashboardOverviewAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<OperationalQueueDto>> GetOperationalQueueAsync(
        CancellationToken cancellationToken = default);

    Task<Result<List<CountTrendPointDto>>> GetOrdersTrendAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<CountTrendPointDto>>> GetCoverageRequestsTrendAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<RevenueTrendPointDto>>> GetInvoiceRevenueTrendAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<RevenueTrendPointDto>>> GetPaymentRevenueTrendAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<StatusBreakdownDto>>> GetOrderStatusBreakdownAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<StatusBreakdownDto>>> GetInstallationStatusBreakdownAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<StatusBreakdownDto>>> GetSupportTicketStatusBreakdownAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<ServicePackagePerformanceDto>>> GetServicePackagePerformanceAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<RegionalDemandDto>>> GetRegionalDemandAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<CustomerGrowthTrendPointDto>>> GetCustomerGrowthTrendAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<InstallationStatusTrendPointDto>>> GetInstallationMonthlyStatusTrendAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<RevenueTrendPointDto>>> GetOutstandingBalanceTrendAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<List<CountTrendPointDto>>> GetFailedPaymentsTrendAsync(
        AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken = default);
}

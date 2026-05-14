using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Reports;
using SmartFuture.Application.Reports.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/admin/reports")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminReportsController : BaseController
{
    private readonly IAdminReportService _service;

    public AdminReportsController(IAdminReportService service)
    {
        _service = service;
    }

    [HttpGet("dashboard-overview")]
    public async Task<IActionResult> DashboardOverview([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetDashboardOverviewAsync(filter, cancellationToken));

    [HttpGet("operational-queue")]
    public async Task<IActionResult> OperationalQueue(CancellationToken cancellationToken)
        => ToActionResult(await _service.GetOperationalQueueAsync(cancellationToken));

    [HttpGet("trends/orders")]
    public async Task<IActionResult> OrdersTrend([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetOrdersTrendAsync(filter, cancellationToken));

    [HttpGet("trends/coverage-requests")]
    public async Task<IActionResult> CoverageRequestsTrend([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetCoverageRequestsTrendAsync(filter, cancellationToken));

    [HttpGet("trends/invoice-revenue")]
    public async Task<IActionResult> InvoiceRevenueTrend([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetInvoiceRevenueTrendAsync(filter, cancellationToken));

    [HttpGet("trends/payment-revenue")]
    public async Task<IActionResult> PaymentRevenueTrend([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetPaymentRevenueTrendAsync(filter, cancellationToken));

    [HttpGet("breakdowns/orders/status")]
    public async Task<IActionResult> OrderStatusBreakdown([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetOrderStatusBreakdownAsync(filter, cancellationToken));

    [HttpGet("breakdowns/installations/status")]
    public async Task<IActionResult> InstallationStatusBreakdown([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetInstallationStatusBreakdownAsync(filter, cancellationToken));

    [HttpGet("breakdowns/support-tickets/status")]
    public async Task<IActionResult> SupportTicketStatusBreakdown([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetSupportTicketStatusBreakdownAsync(filter, cancellationToken));

    [HttpGet("service-package-performance")]
    public async Task<IActionResult> ServicePackagePerformance([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetServicePackagePerformanceAsync(filter, cancellationToken));

    [HttpGet("regional-demand")]
    public async Task<IActionResult> RegionalDemand([FromQuery] AdminDashboardFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetRegionalDemandAsync(filter, cancellationToken));
}

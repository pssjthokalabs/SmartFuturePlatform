using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

/// <summary>
/// Phase 0A/0F — READ-ONLY admin visibility over the recurring-billing
/// engine: run history + detail, schedules, and observational mirrors of the
/// due-invoice / due-retry / suspension-candidate selections.
///
/// DELIBERATELY READ-ONLY: no charge trigger, no run trigger, no mutation,
/// no notifications. The manual test endpoint
/// (<c>POST /api/admin/auto-billing/run-test</c>) remains the only manual
/// path and stays Production-blocked. All endpoints are gated by
/// <see cref="AutoBillingSettings.AdminReportingEnabled"/> (default true);
/// when false they return 503.
/// </summary>
[Route("api/admin/recurring-billing")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminRecurringBillingController : BaseController
{
    private readonly IBillingRunReportService _report;
    private readonly AutoBillingSettings _settings;

    public AdminRecurringBillingController(
        IBillingRunReportService report,
        IOptions<AutoBillingSettings> settings)
    {
        _report = report;
        _settings = settings.Value;
    }

    private IActionResult? DisabledGuard()
    {
        if (_settings.AdminReportingEnabled)
            return null;
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            IsSuccess = false,
            Code = "SERVICE_UNAVAILABLE",
            Message = "Recurring-billing admin reporting is disabled (AutoBilling__AdminReportingEnabled=false)."
        });
    }

    /// <summary>Recent recurring-billing runs (newest first).</summary>
    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns([FromQuery] int take = 25, CancellationToken cancellationToken = default)
        => DisabledGuard() ?? ToActionResult(await _report.GetRecentRunsAsync(take, cancellationToken));

    /// <summary>A single run with parsed stage summary.</summary>
    [HttpGet("runs/{id:guid}")]
    public async Task<IActionResult> GetRun(Guid id, CancellationToken cancellationToken = default)
        => DisabledGuard() ?? ToActionResult(await _report.GetRunByIdAsync(id, cancellationToken));

    /// <summary>Billing schedules (optional ?status= filter: Active/Paused/Cancelled).</summary>
    [HttpGet("schedules")]
    public async Task<IActionResult> GetSchedules(
        [FromQuery] string? status = null, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => DisabledGuard() ?? ToActionResult(await _report.GetSchedulesAsync(status, take, cancellationToken));

    /// <summary>A single billing schedule.</summary>
    [HttpGet("schedules/{id:guid}")]
    public async Task<IActionResult> GetSchedule(Guid id, CancellationToken cancellationToken = default)
        => DisabledGuard() ?? ToActionResult(await _report.GetScheduleByIdAsync(id, cancellationToken));

    /// <summary>
    /// Observational mirror of the Stage 4 suspension-candidate query.
    /// READ-ONLY — computes nothing persistent, sends nothing, suspends nothing.
    /// </summary>
    [HttpGet("candidates/suspension")]
    public async Task<IActionResult> GetSuspensionCandidates([FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => DisabledGuard() ?? ToActionResult(await _report.GetSuspensionCandidatesAsync(take, cancellationToken));

    /// <summary>Observational mirror of the Stage 2 due-invoice selection (read-only).</summary>
    [HttpGet("invoices/due")]
    public async Task<IActionResult> GetDueInvoices([FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => DisabledGuard() ?? ToActionResult(await _report.GetDueInvoicesAsync(take, cancellationToken));

    /// <summary>Observational mirror of the Stage 3 due-retry selection (read-only).</summary>
    [HttpGet("retries/due")]
    public async Task<IActionResult> GetDueRetries([FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => DisabledGuard() ?? ToActionResult(await _report.GetDueRetriesAsync(take, cancellationToken));
}

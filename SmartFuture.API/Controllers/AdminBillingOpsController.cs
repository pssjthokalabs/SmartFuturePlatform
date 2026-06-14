using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.BillingOps;
using SmartFuture.Application.Payments.BillingOps.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

/// <summary>
/// Billing Ops v1 — admin monitoring + manual service-invoice creation.
///
/// Read endpoints respect <see cref="AutoBillingSettings.AdminReportingEnabled"/>
/// (503 when off). The manual-create endpoint additionally requires
/// <see cref="BillingOpsSettings.ManualInvoiceEnabled"/> + the correct
/// confirmation phrase(s). NOTHING here charges, marks paid, calls a
/// provider, or applies a payment.
/// </summary>
[Route("api/admin/billing-ops")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminBillingOpsController : BaseController
{
    private readonly IBillingOpsService _ops;
    private readonly IManualInvoiceService _manualInvoice;
    private readonly ICurrentUserService _currentUser;
    private readonly AutoBillingSettings _autoSettings;
    private readonly BillingOpsSettings _opsSettings;

    public AdminBillingOpsController(
        IBillingOpsService ops,
        IManualInvoiceService manualInvoice,
        ICurrentUserService currentUser,
        IOptions<AutoBillingSettings> autoSettings,
        IOptions<BillingOpsSettings> opsSettings)
    {
        _ops = ops;
        _manualInvoice = manualInvoice;
        _currentUser = currentUser;
        _autoSettings = autoSettings.Value;
        _opsSettings = opsSettings.Value;
    }

    private IActionResult? ReportingDisabledGuard()
    {
        if (_autoSettings.AdminReportingEnabled)
            return null;
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            IsSuccess = false,
            Code = "SERVICE_UNAVAILABLE",
            Message = "Billing Ops reporting is disabled (AutoBilling__AdminReportingEnabled=false)."
        });
    }

    /// <summary>Aggregated overview — live flags, last run, current work counts.</summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken = default)
        => ReportingDisabledGuard() ?? ToActionResult(await _ops.GetDashboardAsync(cancellationToken));

    /// <summary>Per-run activity (time-window approximation; labelled as such).</summary>
    [HttpGet("runs/{runId:guid}/activity")]
    public async Task<IActionResult> GetRunActivity(Guid runId, CancellationToken cancellationToken = default)
        => ReportingDisabledGuard() ?? ToActionResult(await _ops.GetRunActivityAsync(runId, cancellationToken));

    /// <summary>Charge attempts (PaymentInitiations) with optional status/provider/date filters.</summary>
    [HttpGet("charges")]
    public async Task<IActionResult> GetCharges(
        [FromQuery] string? status = null, [FromQuery] string? provider = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => ReportingDisabledGuard() ?? ToActionResult(
            await _ops.GetChargesAsync(status, provider, from, to, take, cancellationToken));

    /// <summary>Retry attempts with optional status/date filters.</summary>
    [HttpGet("retries")]
    public async Task<IActionResult> GetRetries(
        [FromQuery] string? status = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => ReportingDisabledGuard() ?? ToActionResult(
            await _ops.GetRetriesAsync(status, from, to, take, cancellationToken));

    /// <summary>Billing notifications (OutboundNotification) with optional invoice/type/date filters.</summary>
    [HttpGet("notifications")]
    public async Task<IActionResult> GetNotifications(
        [FromQuery] Guid? invoiceId = null, [FromQuery] string? type = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => ReportingDisabledGuard() ?? ToActionResult(
            await _ops.GetNotificationsAsync(invoiceId, type, from, to, take, cancellationToken));

    /// <summary>Attention list — buckets of items needing admin action.</summary>
    [HttpGet("manual-action-required")]
    public async Task<IActionResult> GetManualActionRequired(CancellationToken cancellationToken = default)
        => ReportingDisabledGuard() ?? ToActionResult(await _ops.GetManualActionRequiredAsync(cancellationToken));

    /// <summary>
    /// Manually create a recurring SERVICE invoice. Requires
    /// <c>BillingOps__ManualInvoiceEnabled=true</c> + the correct
    /// confirmation phrase. A duplicate period is blocked (409) unless
    /// force-created (schedule-detached) with a reason + force phrase.
    /// Never charges, never marks paid.
    /// </summary>
    [HttpPost("invoices/manual-service")]
    public async Task<IActionResult> CreateManualServiceInvoice(
        [FromBody] ManualServiceInvoiceRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!_opsSettings.ManualInvoiceEnabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                IsSuccess = false,
                Code = "SERVICE_UNAVAILABLE",
                Message = "Manual service-invoice creation is disabled (BillingOps__ManualInvoiceEnabled=false)."
            });
        }

        var result = await _manualInvoice.CreateManualServiceInvoiceAsync(
            request, _currentUser.UserId, _currentUser.IpAddress, _currentUser.UserAgent, cancellationToken);

        return ToActionResult(result);
    }
}

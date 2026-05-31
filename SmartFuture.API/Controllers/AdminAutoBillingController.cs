using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

/// <summary>
/// Admin endpoints for the Paystack auto-debit pipeline. Provides a
/// safe UAT trigger for the full lifecycle test described in the
/// "UAT live Paystack" spec — pick a customer, dry-run the cycle to
/// verify what would happen, then re-run with <c>dryRun=false</c> to
/// actually charge.
///
/// SAFETY STACK
/// ------------
///   1. <c>[Authorize(Policy = RequireAdmin)]</c> — admin only.
///   2. The endpoint refuses when env is <see cref="IHostEnvironment.IsProduction"/>.
///   3. The endpoint refuses when
///      <see cref="AutoBillingSettings.ManualTestEndpointEnabled"/> is false.
///   4. The endpoint refuses when <see cref="AutoBillingSettings.Enabled"/>
///      is false (defence-in-depth — the service also refuses).
///   5. The service itself also blocks Production runs regardless of
///      how it's invoked.
/// </summary>
[Route("api/admin/auto-billing")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminAutoBillingController : BaseController
{
    private readonly IAutoBillingService _service;
    private readonly AutoBillingSettings _settings;
    private readonly IHostEnvironment _env;

    public AdminAutoBillingController(
        IAutoBillingService service,
        IOptions<AutoBillingSettings> settings,
        IHostEnvironment env)
    {
        _service = service;
        _settings = settings.Value;
        _env = env;
    }

    /// <summary>
    /// Manually run the auto-billing cycle. UAT/dev only. See
    /// <c>AdminAutoBillingRunTestRequestDto</c> for the body shape.
    /// </summary>
    /// <remarks>
    /// Body:
    /// <code>
    /// {
    ///   "userEmail": "vuyani@smartfuture.co.za",   // optional — null = all customers
    ///   "dryRun":    false                          // true = no Paystack calls, no DB writes
    /// }
    /// </code>
    /// </remarks>
    [HttpPost("run-test")]
    public async Task<IActionResult> RunTest(
        [FromBody] AdminAutoBillingRunTestRequestDto request,
        CancellationToken cancellationToken)
    {
        if (_env.IsProduction())
        {
            return ToActionResult(Result<AutoBillingCycleSummaryDto>.Failure(
                ErrorCodes.FORBIDDEN,
                "Manual auto-billing test endpoint is disabled in Production."));
        }
        if (!_settings.ManualTestEndpointEnabled)
        {
            return ToActionResult(Result<AutoBillingCycleSummaryDto>.Failure(
                ErrorCodes.SERVICE_UNAVAILABLE,
                "AutoBilling__ManualTestEndpointEnabled=false. Enable it in UAT config to use this endpoint."));
        }

        request ??= new AdminAutoBillingRunTestRequestDto();
        var result = await _service.RunAutoBillingCycleAsync(
            userEmail: string.IsNullOrWhiteSpace(request.UserEmail) ? null : request.UserEmail.Trim(),
            dryRun: request.DryRun,
            cancellationToken);
        return ToActionResult(result);
    }
}

public class AdminAutoBillingRunTestRequestDto
{
    /// <summary>Optional. Restricts the run to a single customer's invoices.</summary>
    public string? UserEmail { get; set; }

    /// <summary>When true, no Paystack calls fire and no DB writes happen — only the summary of what would happen.</summary>
    public bool DryRun { get; set; } = false;
}

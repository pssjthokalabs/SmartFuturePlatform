using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.API.Controllers;

[Route("api/admin/job-settings")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminJobSettingsController : BaseController
{
    private readonly IJobSettingsService _settingsService;
    private readonly IJobAlertService _alertService;

    public AdminJobSettingsController(IJobSettingsService settingsService, IJobAlertService alertService)
    {
        _settingsService = settingsService;
        _alertService = alertService;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => ToActionResult(await _settingsService.GetAsync(cancellationToken));

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateJobSettingsRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _settingsService.UpdateAsync(request, cancellationToken));

    // Manual digest trigger. `dryRun=true` (the default) reports what
    // WOULD be sent without mailing anyone — always use it first on a
    // live environment.
    [HttpPost("alerts/run")]
    public async Task<IActionResult> RunAlerts([FromQuery] JobAlertFrequency? frequency, [FromQuery] bool dryRun = true, CancellationToken cancellationToken = default)
        => ToActionResult(await _alertService.RunDigestAsync(frequency ?? JobAlertFrequency.Weekly, dryRun, cancellationToken));

    [HttpGet("alerts/logs")]
    public async Task<IActionResult> AlertLogs([FromQuery] Guid? userId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
        => ToActionResult(await _alertService.SearchDeliveryLogsAsync(userId, page, pageSize, cancellationToken));
}

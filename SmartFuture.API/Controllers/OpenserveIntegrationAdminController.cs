using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

// Backs Admin → Integrations → Openserve (the console described in the
// brief: configure/test/monitor/demonstrate the entire integration
// without needing Swagger). Every action here is either read-only
// against Smart Future's own state, or a real-but-safe diagnostic
// Openserve call (Test Connection / Qualification Test / Order Lookup)
// that never creates, updates, or cancels a customer order. Order
// mutation (Synchronize/Retry/Cancel) stays on the existing
// OpenserveOrdersController — this controller links to it, never
// duplicates it.
[Route("api/openserve/admin/integration")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class OpenserveIntegrationAdminController : BaseController
{
    private readonly IOpenserveIntegrationAdminService _adminService;

    public OpenserveIntegrationAdminController(IOpenserveIntegrationAdminService adminService)
    {
        _adminService = adminService;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(CancellationToken cancellationToken)
        => ToActionResult(await _adminService.GetOverviewAsync(cancellationToken));

    [HttpGet("configuration")]
    public async Task<IActionResult> GetConfiguration(CancellationToken cancellationToken)
        => ToActionResult(await _adminService.GetConfigurationAsync(cancellationToken));

    [HttpPut("configuration")]
    public async Task<IActionResult> UpdateConfiguration(
        [FromBody] UpdateOpenserveConfigurationRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _adminService.UpdateConfigurationAsync(request, cancellationToken));

    [HttpPost("readiness-check")]
    public async Task<IActionResult> RunReadinessCheck(CancellationToken cancellationToken)
        => ToActionResult(await _adminService.RunReadinessCheckAsync(cancellationToken));

    [HttpPost("configuration-check")]
    public async Task<IActionResult> RunConfigurationCheck(CancellationToken cancellationToken)
        => ToActionResult(await _adminService.RunConfigurationCheckAsync(cancellationToken));

    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection(CancellationToken cancellationToken)
        => ToActionResult(await _adminService.TestConnectionAsync(cancellationToken));

    [HttpPost("test-qualification")]
    public async Task<IActionResult> TestQualification(
        [FromBody] RunOpenserveQualificationTestRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _adminService.RunQualificationTestAsync(request, cancellationToken));

    [HttpPost("test-order-lookup/{openserveOrderId}")]
    public async Task<IActionResult> TestOrderLookup(string openserveOrderId, CancellationToken cancellationToken)
        => ToActionResult(await _adminService.RunOrderLookupTestAsync(openserveOrderId, cancellationToken));

    [HttpGet("callback-health")]
    public async Task<IActionResult> GetCallbackHealth(CancellationToken cancellationToken)
        => ToActionResult(await _adminService.GetCallbackHealthAsync(cancellationToken));

    [HttpGet("logs")]
    public async Task<IActionResult> SearchLogs([FromQuery] OpenserveIntegrationLogFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _adminService.SearchIntegrationLogsAsync(filter, cancellationToken));
}

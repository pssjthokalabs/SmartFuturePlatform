using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.ServiceChanges;
using SmartFuture.Application.ServiceChanges.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/service-changes")]
public class ServiceChangesController : BaseController
{
    private readonly IServiceChangeRequestService _service;

    public ServiceChangesController(IServiceChangeRequestService service)
    {
        _service = service;
    }

    // ─── Customer ───────────────────────────────────────────────────────

    [HttpGet("mine")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMine([FromQuery] ServiceChangeRequestFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineAsync(filter, cancellationToken));

    [HttpGet("mine/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMineById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineByIdAsync(id, cancellationToken));

    [HttpPost("mine/preview")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> PreviewMine([FromBody] PreviewServiceChangeRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.PreviewMineAsync(request, cancellationToken));

    [HttpPost("mine")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> CreateMine([FromBody] CreateServiceChangeRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateMineAsync(request, cancellationToken));

    [HttpPost("mine/{id:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> CancelMine(Guid id, [FromQuery] string? cancellationReason, CancellationToken cancellationToken)
        => ToActionResult(await _service.CancelMineAsync(id, cancellationReason, cancellationToken));

    // ─── Admin ──────────────────────────────────────────────────────────

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] ServiceChangeRequestFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdminById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPost("admin/{id:guid}/process")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminProcess(Guid id, [FromBody] AdminProcessServiceChangeRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminProcessAsync(id, request, cancellationToken));

    [HttpPost("admin/{id:guid}/reject")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminReject(Guid id, [FromBody] AdminRejectServiceChangeRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminRejectAsync(id, request, cancellationToken));

    [HttpPost("admin/{id:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminCancel(Guid id, [FromBody] AdminCancelServiceChangeRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminCancelAsync(id, request, cancellationToken));
}

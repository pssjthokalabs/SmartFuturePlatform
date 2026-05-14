using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.CoverageRequests;
using SmartFuture.Application.CoverageRequests.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/coverage-requests")]
public class CoverageRequestsController : BaseController
{
    private readonly ICoverageRequestService _service;

    public CoverageRequestsController(ICoverageRequestService service)
    {
        _service = service;
    }

    [HttpGet("mine")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMine([FromQuery] CoverageRequestFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineAsync(filter, cancellationToken));

    [HttpGet("mine/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMineById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineByIdAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Create([FromBody] CreateCoverageRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateMineAsync(request, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.CancelMineAsync(id, cancellationToken));

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] CoverageRequestFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPut("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminUpdate(Guid id, [FromBody] AdminUpdateCoverageRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminUpdateAsync(id, request, cancellationToken));

    [HttpPost("admin/{id:guid}/status")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminUpdateStatus(Guid id, [FromBody] AdminUpdateCoverageRequestStatusDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminUpdateStatusAsync(id, request, cancellationToken));
}

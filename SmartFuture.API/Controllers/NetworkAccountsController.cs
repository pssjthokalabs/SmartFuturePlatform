using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.NetworkAccounts;

namespace SmartFuture.API.Controllers;

[Route("api/network-accounts")]
public class NetworkAccountsController : BaseController
{
    private readonly INetworkAccountService _service;

    public NetworkAccountsController(INetworkAccountService service)
    {
        _service = service;
    }

    [HttpGet("mine")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMine([FromQuery] NetworkAccountFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineAsync(filter, cancellationToken));

    [HttpGet("mine/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMineById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineByIdAsync(id, cancellationToken));

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] NetworkAccountFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPost("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminProvision([FromBody] AdminProvisionNetworkAccountRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.ProvisionForOrderAsync(request?.OrderId ?? Guid.Empty, NetworkAccountSource.AdminManual, cancellationToken));

    [HttpPost("admin/{id:guid}/suspend")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminSuspend(Guid id, [FromBody] AdminSuspendNetworkAccountRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminSuspendAsync(id, request, cancellationToken));

    [HttpPost("admin/{id:guid}/resume")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminResume(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminResumeAsync(id, cancellationToken));

    [HttpPost("admin/{id:guid}/terminate")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminTerminate(Guid id, [FromBody] AdminTerminateNetworkAccountRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminTerminateAsync(id, request, cancellationToken));

    [HttpPost("admin/{id:guid}/change-package")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminChangePackage(Guid id, [FromBody] AdminChangeNetworkAccountPackageRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminChangePackageAsync(id, request, cancellationToken));
}

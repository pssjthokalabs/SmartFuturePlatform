using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.ServicePackages;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/service-packages")]
public class ServicePackagesController : BaseController
{
    private readonly IServicePackageService _service;

    public ServicePackagesController(IServicePackageService service)
    {
        _service = service;
    }

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] ServicePackageFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPost("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> Create([FromBody] CreateServicePackageRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateAsync(request, cancellationToken));

    [HttpPut("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateServicePackageRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("admin/{id:guid}/activate")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.ActivateAsync(id, cancellationToken));

    [HttpPost("admin/{id:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.DeactivateAsync(id, cancellationToken));

    [HttpPost("admin/{id:guid}/archive")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.ArchiveAsync(id, cancellationToken));

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Search([FromQuery] ServicePackageFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchCustomerAsync(filter, cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetCustomerByIdAsync(id, cancellationToken));
}

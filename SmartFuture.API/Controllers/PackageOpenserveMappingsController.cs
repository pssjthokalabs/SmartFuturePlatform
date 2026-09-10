using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

// Admin visibility/management for the ServicePackage -> Openserve
// product mapping catalogue (brief §4 and §14). Read-only listing of
// unmapped fibre packages so admin can see at a glance what would be
// rejected at submission time before Phase 2's order-submission
// trigger ever runs.
[Route("api/openserve/package-mappings")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class PackageOpenserveMappingsController : BaseController
{
    private readonly IPackageOpenserveMappingService _service;

    public PackageOpenserveMappingsController(IPackageOpenserveMappingService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => ToActionResult(await _service.ListAsync(cancellationToken));

    [HttpGet("unmapped")]
    public async Task<IActionResult> ListUnmapped(CancellationToken cancellationToken)
        => ToActionResult(await _service.ListUnmappedFibrePackagesAsync(cancellationToken));

    [HttpGet("by-package/{servicePackageId:guid}")]
    public async Task<IActionResult> GetByServicePackageId(Guid servicePackageId, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetByServicePackageIdAsync(servicePackageId, cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePackageOpenserveMappingRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePackageOpenserveMappingRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.DeleteAsync(id, cancellationToken));
}

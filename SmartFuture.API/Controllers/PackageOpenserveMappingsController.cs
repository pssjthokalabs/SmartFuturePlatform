using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

// Admin management of the ServicePackage -> Openserve product mapping
// (brief §4 and §14) — backs Admin → Integrations → Openserve → Package
// Mappings and the Openserve section of the Fibre package editor. Every
// selectable Openserve value comes from GET catalogue (Appendix D); the
// service rejects anything else. Admin-only at the class level; none of
// these responses carry Openserve configuration or credentials.
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

    [HttpGet("fibre-packages")]
    public async Task<IActionResult> ListFibrePackages([FromQuery] bool includeNonActive, CancellationToken cancellationToken)
        => ToActionResult(await _service.ListFibrePackageMappingsAsync(includeNonActive, cancellationToken));

    [HttpGet("catalogue")]
    public IActionResult GetCatalogue()
        => ToActionResult(SmartFuture.Shared.Results.Result<IReadOnlyList<OpenserveCatalogueProductDto>>.Success(_service.GetCatalogue()));

    [HttpPatch("{id:guid}/enabled")]
    public async Task<IActionResult> SetEnabled(Guid id, [FromBody] SetPackageOpenserveMappingEnabledRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.SetEnabledAsync(id, request?.IsEnabled ?? false, cancellationToken));

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

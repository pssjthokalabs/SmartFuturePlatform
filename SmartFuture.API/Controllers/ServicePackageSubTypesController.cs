using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.ServicePackages;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.API.Controllers;

// Admin management of DB-driven package subtypes (Security → CCTV /
// Intercom / …). The package form's subtype dropdown lists these and the
// inline "+ Add new security type" posts here. No public read endpoint —
// the subtype is projected onto the public ServicePackage DTO itself.
[Route("api/admin/service-package-subtypes")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class ServicePackageSubTypesController : BaseController
{
    private readonly IServicePackageSubTypeService _service;

    public ServicePackageSubTypesController(IServicePackageSubTypeService service)
    {
        _service = service;
    }

    // GET /api/admin/service-package-subtypes?packageType=Security&activeOnly=true
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] ServicePackageType? packageType,
        [FromQuery] bool activeOnly,
        CancellationToken cancellationToken)
        => ToActionResult(await _service.ListAsync(packageType, activeOnly, cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateServicePackageSubTypeRequestDto request,
        CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateServicePackageSubTypeRequestDto request,
        CancellationToken cancellationToken)
        => ToActionResult(await _service.UpdateAsync(id, request, cancellationToken));
}

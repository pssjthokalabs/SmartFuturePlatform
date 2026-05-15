using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Customers.Admin;
using SmartFuture.Application.Customers.Admin.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/customers/admin")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminCustomersController : BaseController
{
    private readonly IAdminCustomerService _service;

    public AdminCustomersController(IAdminCustomerService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] AdminCustomerFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAsync(filter, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetByIdAsync(id, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAdminCustomerRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.ActivateAsync(id, cancellationToken));

    [HttpPost("{id:guid}/suspend")]
    public async Task<IActionResult> Suspend(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.SuspendAsync(id, cancellationToken));
}

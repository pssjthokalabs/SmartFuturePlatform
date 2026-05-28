using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/provisioning-events")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class ProvisioningEventsController : BaseController
{
    private readonly IProvisioningEventService _service;

    public ProvisioningEventsController(IProvisioningEventService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] ProvisioningEventFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAsync(filter, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetByIdAsync(id, cancellationToken));
}

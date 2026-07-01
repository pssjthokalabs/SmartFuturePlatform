using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/admin/billing-day-options")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminBillingDayOptionsController : BaseController
{
    private readonly IBillingDayOptionService _service;

    public AdminBillingDayOptionsController(IBillingDayOptionService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => ToActionResult(await _service.GetAllAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => ToActionResult(await _service.GetByIdAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBillingDayOptionRequestDto request, CancellationToken ct)
        => ToActionResult(await _service.CreateAsync(request, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateBillingDayOptionRequestDto request, CancellationToken ct)
        => ToActionResult(await _service.UpdateAsync(id, request, ct));
}

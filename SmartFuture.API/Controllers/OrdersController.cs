using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/orders")]
public class OrdersController : BaseController
{
    private readonly IOrderService _service;

    public OrdersController(IOrderService service)
    {
        _service = service;
    }

    [HttpGet("mine")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMine([FromQuery] OrderFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineAsync(filter, cancellationToken));

    [HttpGet("mine/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMineById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineByIdAsync(id, cancellationToken));

    // Phase 51 — eligibility probe consumed by SmartFutureApp and the
    // customer portal before launching the order wizard. Server-side
    // gate is still inside `CreateMineAsync` — this endpoint exists so
    // the UI can pre-block with a friendly panel instead of failing
    // late during create.
    [HttpGet("mine/eligibility")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMyEligibility(CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMyEligibilityAsync(cancellationToken));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Create([FromBody] CreateOrderRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateMineAsync(request, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Cancel(Guid id, [FromQuery] string? cancellationReason, CancellationToken cancellationToken)
        => ToActionResult(await _service.CancelMineAsync(id, cancellationReason, cancellationToken));

    // Phase 51 — customer-initiated install-address change. Refuses
    // (CONFLICT) once the install has been assigned or the order is in
    // a terminal state; otherwise re-runs coverage on the new lat/lng
    // and persists the new address onto the Order + any active
    // installation row.
    [HttpPost("mine/{id:guid}/request-address-change")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> RequestAddressChange(Guid id, [FromBody] RequestAddressChangeRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.RequestAddressChangeMineAsync(id, request, cancellationToken));

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] OrderFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPut("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminUpdate(Guid id, [FromBody] AdminUpdateOrderRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminUpdateAsync(id, request, cancellationToken));

    [HttpPost("admin/{id:guid}/status")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminUpdateStatus(Guid id, [FromBody] AdminUpdateOrderStatusDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminUpdateStatusAsync(id, request, cancellationToken));

    // Phase 44 — dedicated narrow endpoint for the "Set Install Date"
    // action. Avoids the address-validation pitfall in AdminUpdate.
    [HttpPost("admin/{id:guid}/install-date")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminSetInstallationDate(Guid id, [FromBody] AdminSetOrderInstallationDateDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminSetInstallationDateAsync(id, request, cancellationToken));

    /// <summary>
    /// Admin "Mark service activated on Openserve". Only legal when
    /// the order is in <c>PendingActivation</c> (installation
    /// Completed AND first monthly invoice Paid). Records activation
    /// date as the billing anchor; sets NextPayDateUtc = anchor + 30 days.
    ///
    /// Body:
    /// <code>
    /// {
    ///   "openserveActivationReference": "OSV-12345",
    ///   "activationNotes": "Activated by admin after OSP confirmation",
    ///   "activationDateUtc": null
    /// }
    /// </code>
    /// </summary>
    [HttpPost("admin/{id:guid}/activate-service")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminActivateService(Guid id, [FromBody] AdminActivateServiceRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminActivateServiceAsync(id, request, cancellationToken));
}

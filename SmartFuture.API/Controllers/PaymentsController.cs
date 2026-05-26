using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/payments")]
public class PaymentsController : BaseController
{
    private readonly IPaymentService _service;
    private readonly OzowNotifyHandler _ozowNotify;

    public PaymentsController(IPaymentService service, OzowNotifyHandler ozowNotify)
    {
        _service = service;
        _ozowNotify = ozowNotify;
    }

    [HttpGet("mine")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMine([FromQuery] PaymentFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineAsync(filter, cancellationToken));

    [HttpGet("mine/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMineById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineByIdAsync(id, cancellationToken));

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] PaymentFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPost("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> Create([FromBody] CreatePaymentRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateAsync(request, cancellationToken));

    [HttpPost("admin/{id:guid}/status")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminUpdateStatus(Guid id, [FromBody] AdminUpdatePaymentStatusDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminUpdateStatusAsync(id, request, cancellationToken));

    // Phase 52 — Ozow notify webhook. Public + anonymous; Ozow POSTs
    // an x-www-form-urlencoded payload here. The handler verifies the
    // hash, looks up the PaymentInitiation by TransactionReference,
    // cross-checks the amount, and applies the status transition via
    // PaymentApplierService (which is itself idempotent). We always
    // return 200 OK with a short text body — Ozow retries on non-2xx,
    // so we don't want to chain-retry on a known bad payload (e.g.
    // hash mismatch on a replay attempt).
    [HttpPost("ozow/notify")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded", "application/json")]
    public async Task<IActionResult> OzowNotify([FromForm] OzowNotifyPayload payload, CancellationToken cancellationToken)
    {
        var outcome = await _ozowNotify.HandleAsync(payload, cancellationToken);
        return Ok(new { accepted = outcome.Accepted, message = outcome.Message });
    }
}

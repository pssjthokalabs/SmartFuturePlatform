using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/payments")]
public class PaymentsController : BaseController
{
    private readonly IPaymentService _service;
    private readonly OzowNotifyHandler _ozowNotify;
    private readonly PayFastNotifyHandler _payFastNotify;
    private readonly PaystackNotifyHandler _paystackNotify;

    public PaymentsController(
        IPaymentService service,
        OzowNotifyHandler ozowNotify,
        PayFastNotifyHandler payFastNotify,
        PaystackNotifyHandler paystackNotify)
    {
        _service = service;
        _ozowNotify = ozowNotify;
        _payFastNotify = payFastNotify;
        _paystackNotify = paystackNotify;
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

    [HttpPost("payfast/notify")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded", "application/json")]
    public async Task<IActionResult> PayFastNotify([FromForm] PayFastNotifyPayload payload, CancellationToken cancellationToken)
    {
        var outcome = await _payFastNotify.HandleAsync(payload, cancellationToken);
        return Ok(new { accepted = outcome.Accepted, message = outcome.Message });
    }

    // Paystack webhook. Public + anonymous; signature is HMAC-SHA512 of
    // the raw JSON body with the merchant secret key, sent in the
    // x-paystack-signature header. We MUST read the body verbatim
    // (no model-binding) so the hash matches Paystack's. Always returns
    // HTTP 200 with a short JSON body — Paystack retries on non-2xx,
    // so we don't want to chain-retry on a known bad payload (e.g.
    // signature mismatch on a replay attempt).
    [HttpPost("paystack/notify")]
    [AllowAnonymous]
    [Consumes("application/json")]
    public async Task<IActionResult> PaystackNotify(CancellationToken cancellationToken)
    {
        string rawBody;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }
        var signature = Request.Headers.TryGetValue("x-paystack-signature", out var sig)
            ? sig.ToString()
            : null;
        var outcome = await _paystackNotify.HandleAsync(rawBody, signature, cancellationToken);
        return Ok(new { accepted = outcome.Accepted, message = outcome.Message });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

/// <summary>
/// Admin emergency-recovery endpoint for Paystack payments where the
/// webhook never arrived (or its signature failed). Given a Paystack
/// reference (the SF-PAY-… token that lives on
/// PaymentInitiation.ProviderReference), the service calls Paystack's
/// /transaction/verify with the server-only secret key and applies
/// the payment via the canonical PaymentApplierService.
///
/// Idempotent: replay-safe — the underlying applier short-circuits
/// on already-Completed payments.
/// </summary>
[Route("api/admin/payments/paystack")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminPaystackReconcileController : BaseController
{
    private readonly IPaystackReconciliationService _service;
    private readonly IPaystackWebhookLogQueryService _logs;

    public AdminPaystackReconcileController(
        IPaystackReconciliationService service,
        IPaystackWebhookLogQueryService logs)
    {
        _service = service;
        _logs = logs;
    }

    /// <summary>
    /// Reconcile a single Paystack payment by reference. Usage:
    /// <code>
    /// {
    ///   "reference": "SF-PAY-2026053001234"
    /// }
    /// </code>
    /// Returns the verify result + before/after invoice state +
    /// override audit so the operator can confirm exactly what was
    /// applied.
    /// </summary>
    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile(
        [FromBody] AdminPaystackReconcileRequestDto request, CancellationToken cancellationToken)
    {
        request ??= new AdminPaystackReconcileRequestDto();
        return ToActionResult(await _service.ReconcileAsync(request.Reference ?? string.Empty, cancellationToken));
    }

    /// <summary>
    /// Query webhook diagnostic rows for a given Paystack reference
    /// (e.g. SF-PAY-2026053001234). Returns oldest-first list of every
    /// inbound webhook attempt against that reference — signature
    /// validity, accept/reject reason, apply outcome, etc. The handler
    /// writes one row PER inbound delivery, so retries show as
    /// separate rows. Empty list means Paystack never reached the API
    /// for that reference.
    /// </summary>
    [HttpGet("webhook-logs")]
    public async Task<IActionResult> ByReference(
        [FromQuery] string reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest(new { error = "reference query param is required." });
        var rows = await _logs.ByReferenceAsync(reference, cancellationToken);
        return Ok(new { count = rows.Count, items = rows });
    }

    /// <summary>
    /// Recent webhook diagnostic rows across all references (newest
    /// first). Useful for spotting bursts of signature-mismatch /
    /// unknown-reference rejections without already knowing what to
    /// search for.
    /// </summary>
    [HttpGet("webhook-logs/recent")]
    public async Task<IActionResult> Recent(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var rows = await _logs.RecentAsync(page, pageSize, cancellationToken);
        return Ok(new { page, pageSize, count = rows.Count, items = rows });
    }
}

public class AdminPaystackReconcileRequestDto
{
    public string? Reference { get; set; }
}

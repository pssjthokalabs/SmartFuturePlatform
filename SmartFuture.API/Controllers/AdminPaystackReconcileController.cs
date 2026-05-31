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

    public AdminPaystackReconcileController(IPaystackReconciliationService service)
    {
        _service = service;
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
}

public class AdminPaystackReconcileRequestDto
{
    public string? Reference { get; set; }
}

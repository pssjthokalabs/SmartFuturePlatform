using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/payment-gateway")]
public class PaymentGatewayController : BaseController
{
    private readonly IPaymentGatewayService _service;
    private readonly IPaystackReconciliationService _paystackReconcile;
    private readonly IPaystackStatusService _paystackStatus;

    public PaymentGatewayController(
        IPaymentGatewayService service,
        IPaystackReconciliationService paystackReconcile,
        IPaystackStatusService paystackStatus)
    {
        _service = service;
        _paystackReconcile = paystackReconcile;
        _paystackStatus = paystackStatus;
    }

    [HttpPost("invoice/initiate")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> InitiateInvoicePayment([FromBody] InitiateInvoicePaymentRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.InitiateInvoicePaymentAsync(request, cancellationToken));

    /// <summary>
    /// Customer-facing verify-and-apply. Called by the portal's
    /// /payment/result page (and the mobile WebView return path) when
    /// the customer comes back from Paystack with outcome=success.
    /// Closes the loop when the webhook hasn't arrived yet.
    ///
    /// Anonymous BUT safe:
    ///   1. Reference must exist as a SmartFuture PaymentInitiation row
    ///      — attacker can't fabricate one.
    ///   2. Paystack /transaction/verify must confirm a PAID transaction
    ///      with matching amount + currency — attacker can't forge that.
    ///   3. PaymentApplierService is idempotent — replay is a no-op.
    ///   4. No PII is returned — only invoice number / status.
    /// </summary>
    [HttpPost("paystack/verify-and-apply")]
    [AllowAnonymous]
    public async Task<IActionResult> PaystackVerifyAndApply(
        [FromBody] PaystackVerifyAndApplyRequestDto request, CancellationToken cancellationToken)
    {
        request ??= new PaystackVerifyAndApplyRequestDto();
        return ToActionResult(await _paystackReconcile.ReconcileAsync(request.Reference ?? string.Empty, cancellationToken));
    }

    /// <summary>
    /// Read-only status check for a Paystack reference. Mobile + portal
    /// call this on a short poll (every few seconds for ~30s) after the
    /// first verify-and-apply so they can render "Order submitted" the
    /// moment the apply path finishes, without hammering Paystack's
    /// <c>/transaction/verify</c> endpoint on every tick.
    ///
    /// Anonymous BUT safe:
    ///   1. Reference is the auth — attacker must guess a valid
    ///      SF-INTENT-… or SF-PAY-… reference.
    ///   2. Response is a pure read of SmartFuture DB rows — no
    ///      Paystack call, no state mutation, no audit log.
    ///   3. No PII is returned — only invoice number / order number /
    ///      payment status. Same surface the verify-and-apply response
    ///      already exposes anonymously.
    /// </summary>
    [HttpGet("paystack/status")]
    [AllowAnonymous]
    public async Task<IActionResult> PaystackStatus(
        [FromQuery] string? reference, CancellationToken cancellationToken)
        => ToActionResult(await _paystackStatus.GetStatusAsync(reference ?? string.Empty, cancellationToken));

    /// <summary>
    /// Provider-agnostic alias for the status read above. The
    /// underlying service already handles SF-INTENT-… references for
    /// BOTH Paystack and PayFast intents (the OrderIntent lookup is
    /// not filtered by Provider), so this endpoint is just a cleaner
    /// URL for callers that aren't Paystack-specific.
    ///
    /// Use this from the mobile new-order PayFast path — calling the
    /// Paystack-named endpoint there worked but was misleading and
    /// surfaced in support tickets as "PayFast verified via Paystack
    /// endpoint". The verify-and-apply POST above stays Paystack-only
    /// (it does a real Paystack /transaction/verify); PayFast intents
    /// settle via the PayFast ITN webhook and only need to read
    /// status, never force-verify.
    /// </summary>
    [HttpGet("order-intent/status")]
    [AllowAnonymous]
    public async Task<IActionResult> OrderIntentStatus(
        [FromQuery] string? reference, CancellationToken cancellationToken)
        => ToActionResult(await _paystackStatus.GetStatusAsync(reference ?? string.Empty, cancellationToken));

    [HttpGet("admin/initiations")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] PaymentInitiationFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/initiations/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));
}

public class PaystackVerifyAndApplyRequestDto
{
    /// <summary>SF-PAY-… reference returned by the Paystack redirect (or webhook).</summary>
    public string? Reference { get; set; }
}

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

    public PaymentGatewayController(
        IPaymentGatewayService service,
        IPaystackReconciliationService paystackReconcile)
    {
        _service = service;
        _paystackReconcile = paystackReconcile;
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

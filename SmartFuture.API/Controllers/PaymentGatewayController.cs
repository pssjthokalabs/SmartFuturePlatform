using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/payment-gateway")]
public class PaymentGatewayController : BaseController
{
    private readonly IPaymentGatewayService _service;

    public PaymentGatewayController(IPaymentGatewayService service)
    {
        _service = service;
    }

    [HttpPost("invoice/initiate")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> InitiateInvoicePayment([FromBody] InitiateInvoicePaymentRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.InitiateInvoicePaymentAsync(request, cancellationToken));

    [HttpGet("admin/initiations")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] PaymentInitiationFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/initiations/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

[Route("api/payment-mandates")]
[Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
public class PaymentMandatesController : BaseController
{
    private readonly ICustomerPaymentMandateService _service;
    private readonly ICurrentUserService _currentUser;

    public PaymentMandatesController(ICustomerPaymentMandateService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    [HttpGet("mine")]
    public async Task<IActionResult> GetMine(CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result<IReadOnlyList<CustomerPaymentMandateDto>>.Failure(
                ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return ToActionResult(await _service.GetMineAsync(_currentUser.UserId.Value, cancellationToken));
    }

    [HttpPost("mine/{id:guid}/default")]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result<CustomerPaymentMandateDto>.Failure(
                ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return ToActionResult(await _service.SetDefaultAsync(_currentUser.UserId.Value, id, cancellationToken));
    }

    [HttpDelete("mine/{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return ToActionResult(await _service.DeactivateAsync(_currentUser.UserId.Value, id, cancellationToken));
    }

    [HttpGet("mine/settings")]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result<AutoBillingPreferenceDto>.Failure(
                ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return ToActionResult(await _service.GetAutoBillingPreferenceAsync(_currentUser.UserId.Value, cancellationToken));
    }

    [HttpPut("mine/settings")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] UpdateAutoBillingPreferenceRequestDto request, CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result<AutoBillingPreferenceDto>.Failure(
                ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return ToActionResult(await _service.UpdateAutoBillingPreferenceAsync(
            _currentUser.UserId.Value, request, cancellationToken));
    }
}

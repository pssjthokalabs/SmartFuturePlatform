using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.CustomerProfiles;
using SmartFuture.Application.CustomerProfiles.Dtos;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

[Route("api/customer-profiles")]
[Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
public class CustomerProfilesController : BaseController
{
    private readonly ICustomerProfileService _service;
    private readonly ICurrentUserService _currentUser;

    public CustomerProfilesController(ICustomerProfileService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    [HttpGet("mine")]
    public async Task<IActionResult> GetMine()
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result<CustomerProfileDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return ToActionResult(await _service.GetMineAsync(_currentUser.UserId.Value));
    }

    [HttpPut("mine")]
    public async Task<IActionResult> PutMine([FromBody] UpdateCustomerProfileRequestDto request)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result<CustomerProfileDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return ToActionResult(await _service.CreateOrUpdateMineAsync(_currentUser.UserId.Value, request));
    }
}

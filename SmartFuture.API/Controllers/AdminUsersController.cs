using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Users.Admin;
using SmartFuture.Application.Users.Admin.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/users/admin")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminUsersController : BaseController
{
    private readonly IAdminUsersService _service;

    public AdminUsersController(IAdminUsersService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] AdminUsersFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAsync(filter, cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAdminUserRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateAsync(request, cancellationToken));

    // Phase 41 — narrow Edit User flow. Field-level permissions
    // (email/phone restricted to Super Admins, Super Admin rows off-
    // limits to non-Super-Admin actors) are enforced in the service.
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAdminUserRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.UpdateAsync(id, request, cancellationToken));

    // Phase 56 — Change user role (1-to-1 role contract). SuperAdmin
    // only; the service re-checks the actor's role + last-Super-Admin
    // invariant so we never trust the UI for permission decisions.
    [HttpPut("{id:guid}/role")]
    public async Task<IActionResult> ChangeRole(Guid id, [FromBody] ChangeAdminUserRoleRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.ChangeRoleAsync(id, request, cancellationToken));
}

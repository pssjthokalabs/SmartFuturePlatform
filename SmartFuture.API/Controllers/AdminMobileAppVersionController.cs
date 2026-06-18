using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.AppVersion;
using SmartFuture.Application.AppVersion.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

/// <summary>
/// Admin management of the DB-backed mobile app-version rules
/// (Admin Portal → Settings → Mobile App Versions). RequireAdmin.
/// </summary>
[Route("api/admin/mobile-app-version")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminMobileAppVersionController : BaseController
{
    private readonly IMobileAppVersionService _service;
    private readonly ICurrentUserService _currentUser;

    public AdminMobileAppVersionController(IMobileAppVersionService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    [HttpGet("rules")]
    public async Task<IActionResult> GetRules(CancellationToken cancellationToken = default)
        => ToActionResult(await _service.GetRulesAsync(cancellationToken));

    [HttpGet("rules/{id:guid}")]
    public async Task<IActionResult> GetRule(Guid id, CancellationToken cancellationToken = default)
        => ToActionResult(await _service.GetRuleByIdAsync(id, cancellationToken));

    [HttpPost("rules")]
    public async Task<IActionResult> CreateRule(
        [FromBody] UpsertMobileAppVersionRuleRequestDto request, CancellationToken cancellationToken = default)
        => ToActionResult(await _service.CreateRuleAsync(request, _currentUser.UserId, cancellationToken));

    [HttpPut("rules/{id:guid}")]
    public async Task<IActionResult> UpdateRule(
        Guid id, [FromBody] UpsertMobileAppVersionRuleRequestDto request, CancellationToken cancellationToken = default)
        => ToActionResult(await _service.UpdateRuleAsync(id, request, _currentUser.UserId, cancellationToken));

    [HttpPost("rules/{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken = default)
        => ToActionResult(await _service.SetEnabledAsync(id, true, _currentUser.UserId, cancellationToken));

    [HttpPost("rules/{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken = default)
        => ToActionResult(await _service.SetEnabledAsync(id, false, _currentUser.UserId, cancellationToken));
}

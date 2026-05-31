using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Installations;
using SmartFuture.Application.Installations.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

/// <summary>
/// Technician-scoped endpoints. Every action is automatically filtered
/// to the calling user's <c>TechnicianUserId</c> on the service layer,
/// so a technician cannot see or mutate another technician's
/// installations even if they guess the id.
///
/// Admins / SuperAdmins also satisfy <c>RequireTechnician</c> so they
/// can exercise these endpoints from Swagger in dev without juggling
/// a second login.
/// </summary>
[Route("api/technician/installations")]
[Authorize(Policy = AuthorizationPolicies.RequireTechnician)]
public class TechnicianInstallationsController : BaseController
{
    private readonly IInstallationService _service;

    public TechnicianInstallationsController(IInstallationService service)
    {
        _service = service;
    }

    /// <summary>List installations assigned to the calling technician.</summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] InstallationFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAssignedToMeAsync(filter, cancellationToken));

    /// <summary>Get a single installation — must be assigned to the calling technician.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAssignedToMeByIdAsync(id, cancellationToken));

    /// <summary>
    /// Technician self-update — InProgress / Completed / Failed.
    /// Completing requires router make/model + serial + installed
    /// location notes. Completing triggers the same downstream pipeline
    /// as an admin completion (first monthly invoice + auto-debit),
    /// but flips Order to <c>PendingPayment</c> — never <c>Active</c>.
    /// </summary>
    [HttpPost("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] TechnicianUpdateInstallationStatusDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.TechnicianUpdateStatusAsync(id, request, cancellationToken));
}

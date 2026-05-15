using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Dashboard;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/admin/dashboard")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminDashboardController : BaseController
{
    private readonly IAdminDashboardService _service;

    public AdminDashboardController(IAdminDashboardService service)
    {
        _service = service;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
        => ToActionResult(await _service.GetSummaryAsync(cancellationToken));
}

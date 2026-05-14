using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Admin;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/admin/system")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminSystemController : BaseController
{
    private readonly IAdminSystemService _service;

    public AdminSystemController(IAdminSystemService service)
    {
        _service = service;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
        => ToActionResult(await _service.GetSystemSummaryAsync(cancellationToken));
}

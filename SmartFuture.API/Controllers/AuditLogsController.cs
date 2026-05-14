using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/audit-logs")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AuditLogsController : BaseController
{
    private readonly IAuditService _auditService;

    public AuditLogsController(IAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet("admin")]
    public async Task<IActionResult> Search([FromQuery] AuditLogFilterRequestDto filter)
        => ToActionResult(await _auditService.SearchAsync(filter));
}

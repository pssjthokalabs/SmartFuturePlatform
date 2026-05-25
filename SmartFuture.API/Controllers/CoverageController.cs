using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;

namespace SmartFuture.API.Controllers;

// Public coverage-check endpoint. Anonymous on purpose — the marketing
// website calls this before the visitor has any account. Reusable by
// SmartFutureApp / SmartFuturePortal in later phases without code
// changes.
[Route("api/coverage")]
public class CoverageController : BaseController
{
    private readonly ICoverageCheckService _service;

    public CoverageController(ICoverageCheckService service)
    {
        _service = service;
    }

    [HttpPost("check")]
    [AllowAnonymous]
    public async Task<IActionResult> Check([FromBody] CoverageCheckRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CheckAsync(request, cancellationToken));
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Shared.Errors;

namespace SmartFuture.API.Controllers;

// Public coverage-check endpoint. Anonymous on purpose — the marketing
// website calls this before the visitor has any account. Reusable by
// SmartFutureApp / SmartFuturePortal in later phases without code
// changes.
[Route("api/coverage")]
public class CoverageController : BaseController
{
    private readonly ICoverageCheckService _service;
    private readonly ILogger<CoverageController> _logger;

    public CoverageController(ICoverageCheckService service, ILogger<CoverageController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    [HttpPost("check")]
    [AllowAnonymous]
    public async Task<IActionResult> Check([FromBody] CoverageCheckRequestDto request, CancellationToken cancellationToken)
    {
        // Belt-and-suspenders: the service already has a top-level
        // try/catch and returns Result.Failure for every known mode,
        // but Cloudflare reported a 502 HTML page in UAT — which
        // means the request didn't return JSON. That can only happen
        // if something escaped every catch (process crash, async
        // void, or — the most likely cause — the request taking
        // longer than the Cloudflare origin timeout). This catch
        // guarantees a JSON envelope leaves the controller even if
        // the service itself somehow throws.
        try
        {
            return ToActionResult(await _service.CheckAsync(request, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller hung up (browser navigated away, request abort).
            // Don't log — this is a normal flow, not a server bug.
            return StatusCode(StatusCodes.Status499ClientClosedRequest, new
            {
                IsSuccess = false,
                Code      = ErrorCodes.EXCEPTION,
                Message   = "Request cancelled."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coverage check controller-level failure (escaped service catch).");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                IsSuccess = false,
                Code      = ErrorCodes.UPSTREAM_UNAVAILABLE,
                Message   = "Coverage service failed unexpectedly. Please try again shortly."
            });
        }
    }
}

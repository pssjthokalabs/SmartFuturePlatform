using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.AppVersion;
using SmartFuture.Application.AppVersion.Dtos;

namespace SmartFuture.API.Controllers;

// Public mobile-app version / update-check endpoint. Anonymous on
// purpose — the SmartFutureApp calls it on startup, BEFORE login, to
// decide whether to recommend or force an update. Carries no secrets.
//
// Now DB-backed (MobileAppVersionRule, admin-editable in the portal),
// with the legacy appsettings `MobileAppVersion` policy as a fallback
// when no rule is seeded. The response is BACKWARD-SAFE: it carries the
// new evaluated flat fields AND the legacy android/ios/message/forceMessage
// block, so old app builds (client-side compare) and new builds
// (server-evaluated) both work off one payload.
[Route("api/app-version")]
public class MobileAppVersionController : BaseController
{
    private readonly IMobileAppVersionService _service;

    public MobileAppVersionController(IMobileAppVersionService service)
    {
        _service = service;
    }

    // GET /api/app-version/mobile?platform=android&channel=google&version=1.0.1&buildNumber=12
    // All query params optional — old builds call it with none and read
    // the legacy android/ios block.
    [HttpGet("mobile")]
    [AllowAnonymous]
    public async Task<ActionResult<MobileAppVersionCheckResponseDto>> GetMobile(
        [FromQuery] string? platform = null,
        [FromQuery] string? channel = null,
        [FromQuery] string? version = null,
        [FromQuery] int? buildNumber = null,
        CancellationToken cancellationToken = default)
        => Ok(await _service.CheckAsync(platform, channel, version, buildNumber, cancellationToken));
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartFuture.Application.AppVersion;

namespace SmartFuture.API.Controllers;

// Public mobile-app version / update-check endpoint. Anonymous on
// purpose — the SmartFutureApp calls it on startup, BEFORE login, to
// decide whether to recommend or force an update. Carries no secrets.
//
// Values come from the `MobileAppVersion` section of appsettings.json /
// appsettings.Production.json (NOT env vars), so update flags can be
// changed with a quick config-only edit on the host. IOptionsSnapshot
// re-reads config per request, so an appsettings edit takes effect
// without restarting the app (reloadOnChange is on by default).
[Route("api/app-version")]
public class MobileAppVersionController : BaseController
{
    private readonly IOptionsSnapshot<MobileAppVersionSettings> _options;

    public MobileAppVersionController(IOptionsSnapshot<MobileAppVersionSettings> options)
    {
        _options = options;
    }

    // GET /api/app-version/mobile
    // Returns the full version policy (camelCase JSON) for both platforms.
    [HttpGet("mobile")]
    [AllowAnonymous]
    public ActionResult<MobileAppVersionSettings> GetMobile()
        => Ok(_options.Value);
}

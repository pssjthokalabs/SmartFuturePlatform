using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

// Public Job Opportunities surface — consumed by BOTH the marketing
// website and the mobile app. Anonymous by design so job pages stay
// crawlable and shareable.
//
// Subscriber gating: when JobSettings.JobDetailsSubscribersOnly is on,
// the DETAIL endpoint withholds the long-form/apply fields from
// non-subscribers and sets `requiresSubscription: true` on the payload.
// The caller still gets the card data, so the page renders a teaser plus
// a sign-up call to action instead of a bare 403.
[Route("api/jobs")]
[AllowAnonymous]
public class JobsController : BaseController
{
    private readonly IJobOpportunityService _service;
    private readonly IJobSettingsService _settingsService;

    public JobsController(IJobOpportunityService service, IJobSettingsService settingsService)
    {
        _service = service;
        _settingsService = settingsService;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] JobOpportunityFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchPublicAsync(filter, cancellationToken));

    // Accepts either the slug ("senior-developer-acme") or the raw Guid,
    // so links shared before a slug change keep resolving.
    [HttpGet("{slugOrId}")]
    public async Task<IActionResult> GetOne(string slugOrId, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetPublicBySlugOrIdAsync(slugOrId, CallerIsSubscriber(), cancellationToken));

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
        => ToActionResult(await _service.GetCategoriesAsync(cancellationToken));

    [HttpGet("locations")]
    public async Task<IActionResult> GetLocations(CancellationToken cancellationToken)
        => ToActionResult(await _service.GetLocationsAsync(cancellationToken));

    [HttpGet("sources")]
    public async Task<IActionResult> GetSources(CancellationToken cancellationToken)
        => ToActionResult(await _service.GetSourceFacetsAsync(cancellationToken));

    // Lets the website/app render (or hide) the module shell and decide
    // whether to show the "subscribe for full details" call to action
    // before it fetches any job.
    [HttpGet("settings")]
    public async Task<IActionResult> GetPublicSettings(CancellationToken cancellationToken)
        => ToActionResult(await _settingsService.GetPublicAsync(cancellationToken));

    // A JWT is optional on this controller: an anonymous caller simply
    // isn't a subscriber. Admins count as subscribers so support staff
    // can see exactly what a subscriber sees.
    private bool CallerIsSubscriber()
        => User?.Identity?.IsAuthenticated == true
            && (User.IsInRole(SystemRoles.JobSubscriber) || User.IsInRole(SystemRoles.Admin) || User.IsInRole(SystemRoles.SuperAdmin));
}

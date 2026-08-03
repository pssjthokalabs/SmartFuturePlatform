using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.API.Controllers;

// Admin moderation surface for imported + manually captured jobs.
[Route("api/admin/jobs")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminJobsController : BaseController
{
    private readonly IJobOpportunityService _service;
    private readonly IJobImportService _importService;

    public AdminJobsController(IJobOpportunityService service, IJobImportService importService)
    {
        _service = service;
        _importService = importService;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] AdminJobOpportunityFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateJobOpportunityRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateJobOpportunityRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.PublishAsync(id, cancellationToken));

    [HttpPost("{id:guid}/hide")]
    public async Task<IActionResult> Hide(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.HideAsync(id, cancellationToken));

    [HttpPost("{id:guid}/expire")]
    public async Task<IActionResult> Expire(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.ExpireAsync(id, cancellationToken));

    // Soft delete — the row stays for de-duplication history so the
    // crawler can't silently re-import it as new.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.DeleteAsync(id, cancellationToken));

    // Maintenance action: flip every Active job whose closing date has
    // passed to Expired.
    [HttpPost("expire-closed")]
    public async Task<IActionResult> ExpireClosed(CancellationToken cancellationToken)
        => ToActionResult(await _service.ExpireClosedJobsAsync(cancellationToken));

    // "Refresh all" from the jobs dashboard. Ignores each source's
    // crawl schedule — the admin asked for it now.
    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshAll(CancellationToken cancellationToken)
        => ToActionResult(await _importService.RunAllAsync(JobImportRunTrigger.Manual, onlyDue: false, cancellationToken));

    [HttpGet("import-runs")]
    public async Task<IActionResult> SearchImportRuns([FromQuery] JobImportRunFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _importService.SearchRunsAsync(filter, cancellationToken));
}

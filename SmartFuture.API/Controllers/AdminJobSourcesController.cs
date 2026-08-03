using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.API.Controllers;

[Route("api/admin/job-sources")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminJobSourcesController : BaseController
{
    private readonly IJobSourceService _service;
    private readonly IJobImportService _importService;

    public AdminJobSourcesController(IJobSourceService service, IJobImportService importService)
    {
        _service = service;
        _importService = importService;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] JobSourceFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAsync(filter, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetByIdAsync(id, cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateJobSourceRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateJobSourceRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.SetActiveAsync(id, true, cancellationToken));

    [HttpPost("{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.SetActiveAsync(id, false, cancellationToken));

    // Crawl this one source now. A source that blocks us still returns
    // 200 with a failed run in the payload — "we tried and were refused"
    // is information the admin needs, not a server error.
    [HttpPost("{id:guid}/refresh")]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _importService.RunSourceAsync(id, JobImportRunTrigger.Manual, cancellationToken));

    // Repair hatch: drop this source's untouched imported jobs so a
    // corrected crawl can re-create them. Manually-edited rows survive.
    [HttpPost("{id:guid}/purge-imported-jobs")]
    public async Task<IActionResult> PurgeImportedJobs(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _importService.PurgeImportedJobsAsync(id, cancellationToken));

    [HttpGet("{id:guid}/import-runs")]
    public async Task<IActionResult> ImportRuns(Guid id, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
        => ToActionResult(await _importService.SearchRunsAsync(new JobImportRunFilterRequestDto { SourceId = id, Page = page, PageSize = pageSize }, cancellationToken));
}

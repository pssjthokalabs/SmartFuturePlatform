using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

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

    // Start a crawl of this one source. ASYNCHRONOUS: returns as soon as
    // the run row exists, then the background worker does the crawling.
    //
    // WHY NOT SYNCHRONOUS, AND WHY NOT A BIGGER TIMEOUT
    //
    // A refresh fetches one page per Max Pages plus one page per job,
    // sequentially, each fetch up to 20s. A large source is minutes of
    // work. Held inside this request it could be killed by any of five
    // layers we do not control — Cloudflare, IIS/ANCM, the browser, an
    // app-pool recycle, or just a slow job board — and each one fails
    // differently for the admin. The Cloudflare cut was the worst: a
    // severed connection writes no response and therefore no CORS
    // header, so the browser blamed CORS for what was really a timeout.
    // Raising the deadline only changes which layer kills it.
    //
    // So the crawl is no longer in the request at all. There is nothing
    // left here to time out.
    [HttpPost("{id:guid}/refresh")]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken cancellationToken)
    {
        var result = await _importService.QueueSourceRefreshAsync(id, JobImportRunTrigger.Manual, cancellationToken);
        if (!result.IsSuccess) return ToActionResult(result);

        // 202: accepted, not finished. The body carries the runId the
        // portal polls.
        return Accepted(new { result.IsSuccess, result.Message, result.Data });
    }

    // Poll target for a single run. The list endpoint below already
    // supports this, but polling one row by id is cheaper and makes the
    // portal's job obvious.
    [HttpGet("{id:guid}/import-runs/{runId:guid}")]
    public async Task<IActionResult> ImportRun(Guid id, Guid runId, CancellationToken cancellationToken)
        => ToActionResult(await _importService.GetRunAsync(runId, cancellationToken));

    // Read-only blast radius for a permanent delete. Modifies nothing.
    [HttpGet("{id:guid}/delete-preview")]
    public async Task<IActionResult> DeletePreview(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetDeletePreviewAsync(id, cancellationToken));

    // Permanently removes the source and the jobs it imported.
    // deleteManuallyEditedJobs defaults to FALSE — hand-curated rows are
    // preserved and detached unless the caller explicitly opts in.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool deleteManuallyEditedJobs, CancellationToken cancellationToken)
        => ToActionResult(await _service.DeleteAsync(id, deleteManuallyEditedJobs, cancellationToken));

    // Repair hatch: drop this source's untouched imported jobs so a
    // corrected crawl can re-create them. Manually-edited rows survive.
    [HttpPost("{id:guid}/purge-imported-jobs")]
    public async Task<IActionResult> PurgeImportedJobs(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _importService.PurgeImportedJobsAsync(id, cancellationToken));

    [HttpGet("{id:guid}/import-runs")]
    public async Task<IActionResult> ImportRuns(Guid id, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
        => ToActionResult(await _importService.SearchRunsAsync(new JobImportRunFilterRequestDto { SourceId = id, Page = page, PageSize = pageSize }, cancellationToken));
}

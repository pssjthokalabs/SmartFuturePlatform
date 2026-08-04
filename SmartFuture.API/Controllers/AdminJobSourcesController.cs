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

    // Crawl this one source now. A source that blocks us still returns
    // 200 with a failed run in the payload — "we tried and were refused"
    // is information the admin needs, not a server error.
    //
    // WHY THE DEADLINE: the crawl (fetch source → fetch each archive page →
    // fetch each job detail page, every fetch up to 20s) runs synchronously
    // inside this request. A slow or large source can outlive the upstream
    // Cloudflare/IIS proxy timeout (~100s); the connection is then cut before
    // we can respond, and the browser reports a *bogus* CORS failure
    // (net::ERR_FAILED, "No 'Access-Control-Allow-Origin' header") instead of
    // our JSON — because no response, and therefore no CORS header, is ever
    // written. Bounding the crawl at 60s (comfortably under the proxy cut-off)
    // guarantees the endpoint returns a normal JSON result — success, a failed
    // run, or the timeout error below — every one of which flows through the
    // standard pipeline and carries CORS headers the admin portal can read.
    private const int RefreshDeadlineSeconds = 60;

    [HttpPost("{id:guid}/refresh")]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(RefreshDeadlineSeconds));
        try
        {
            return ToActionResult(await _importService.RunSourceAsync(id, JobImportRunTrigger.Manual, cts.Token));
        }
        // Our deadline fired (NOT a genuine client disconnect — that leaves
        // `cancellationToken` cancelled and we let it propagate). Convert the
        // cancellation into a readable JSON error so the admin sees the real
        // cause instead of a browser CORS failure.
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ToActionResult(Result<JobImportRunDto>.Failure(
                ErrorCodes.UPSTREAM_UNAVAILABLE,
                $"The source did not finish crawling within {RefreshDeadlineSeconds}s and was stopped so the request could return. " +
                "The source is slow or large — lower its MaxPagesPerRun / MaxJobsPerRun, then try again."));
        }
    }

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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.API.Controllers;

// Admin view of job subscribers. Read-only on purpose — an admin can
// inspect a profile and read the CV, but cannot edit or delete a
// subscriber's documents from here.
//
// Users who are BOTH Customer and JobSubscriber appear here AND in the
// existing Users/Customers pages; neither view hides them from the
// other, and the role pills say which is which.
[Route("api/admin/job-subscribers")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminJobSubscribersController : BaseController
{
    private readonly IAdminJobSubscriberService _service;
    private readonly IJobAlertService _alertService;

    public AdminJobSubscribersController(IAdminJobSubscriberService service, IJobAlertService alertService)
    {
        _service = service;
        _alertService = alertService;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] AdminJobSubscriberFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAsync(filter, cancellationToken));

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Get(Guid userId, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetByUserIdAsync(userId, cancellationToken));

    // Streams the document through the API. Every call is audited with
    // the acting admin's id — CVs are personal data.
    [HttpGet("{userId:guid}/cv")]
    public async Task<IActionResult> DownloadCv(Guid userId, CancellationToken cancellationToken)
        => await Download(userId, JobSubscriberDocumentType.Cv, cancellationToken);

    [HttpGet("{userId:guid}/cover-letter")]
    public async Task<IActionResult> DownloadCoverLetter(Guid userId, CancellationToken cancellationToken)
        => await Download(userId, JobSubscriberDocumentType.CoverLetter, cancellationToken);

    private async Task<IActionResult> Download(Guid userId, JobSubscriberDocumentType documentType, CancellationToken cancellationToken)
    {
        var result = await _service.DownloadDocumentAsync(userId, documentType, cancellationToken);
        if (!result.IsSuccess) return ToActionResult(result);

        return File(result.Data!.Content, result.Data.ContentType, result.Data.FileName);
    }

    // Short-lived pre-signed URL for in-browser preview. Streaming
    // through the API fights a PDF viewer's range requests, so the
    // preview path hands the viewer a direct, expiring link instead.
    [HttpGet("{userId:guid}/cv/preview-url")]
    public async Task<IActionResult> CvPreviewUrl(Guid userId, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetDocumentPreviewUrlAsync(userId, JobSubscriberDocumentType.Cv, cancellationToken));

    [HttpGet("{userId:guid}/cover-letter/preview-url")]
    public async Task<IActionResult> CoverLetterPreviewUrl(Guid userId, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetDocumentPreviewUrlAsync(userId, JobSubscriberDocumentType.CoverLetter, cancellationToken));

    // What this subscriber's next digest would contain. Sends nothing.
    [HttpGet("{userId:guid}/alert-preview")]
    public async Task<IActionResult> AlertPreview(Guid userId, [FromQuery] JobAlertFrequency? frequency, CancellationToken cancellationToken)
        => ToActionResult(await _alertService.PreviewMatchesAsync(userId, frequency ?? JobAlertFrequency.Weekly, cancellationToken));

    [HttpGet("{userId:guid}/alert-logs")]
    public async Task<IActionResult> AlertLogs(Guid userId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
        => ToActionResult(await _alertService.SearchDeliveryLogsAsync(userId, page, pageSize, cancellationToken));
}

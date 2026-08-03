using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Auth;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

// Job seeker self-service. Enrolment is anonymous-friendly; everything
// under /me requires the JobSubscriber role.
//
// There is deliberately NO /login here — job subscribers sign in through
// the existing POST /api/auth/login like every other user. One auth
// system, one token, one set of security rules.
[Route("api/job-subscribers")]
public class JobSubscribersController : BaseController
{
    private readonly IJobSubscriberService _service;
    private readonly IAuthService _authService;
    private readonly ICurrentUserService _currentUser;

    public JobSubscribersController(IJobSubscriberService service, IAuthService authService, ICurrentUserService currentUser)
    {
        _service = service;
        _authService = authService;
        _currentUser = currentUser;
    }

    // Enrolment. Works three ways:
    //   • Signed in            → adds JobSubscriber to the caller.
    //   • Anonymous, new email → creates the user.
    //   • Anonymous, known email + correct password → adds the role.
    // Never returns a bare "email already taken" dead end.
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterJobSubscriberRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.RegisterAsync(request, _currentUser.UserId, cancellationToken));

    // Convenience alias for the "existing customer wants job access"
    // journey — same handler, but the intent reads clearly in the client.
    [HttpPost("enrol")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Enrol([FromBody] RegisterJobSubscriberRequestDto? request, CancellationToken cancellationToken)
        => ToActionResult(await _service.RegisterAsync(request ?? new RegisterJobSubscriberRequestDto(), _currentUser.UserId, cancellationToken));

    // Job subscribers use the shared login. Exposed here so the jobs
    // client has a single base path to talk to; it delegates verbatim.
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
        => ToActionResult(await _authService.LoginAsync(request));

    [HttpGet("me")]
    [Authorize(Policy = AuthorizationPolicies.RequireJobSubscriber)]
    public async Task<IActionResult> GetMe(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null) return Unauthenticated<JobSubscriberMeDto>();
        return ToActionResult(await _service.GetMeAsync(_currentUser.UserId.Value, cancellationToken));
    }

    [HttpPost("me/profile")]
    [Authorize(Policy = AuthorizationPolicies.RequireJobSubscriber)]
    public async Task<IActionResult> PostProfile([FromBody] UpsertJobSubscriberProfileRequestDto request, CancellationToken cancellationToken)
        => await UpsertProfile(request, cancellationToken);

    [HttpPut("me/profile")]
    [Authorize(Policy = AuthorizationPolicies.RequireJobSubscriber)]
    public async Task<IActionResult> PutProfile([FromBody] UpsertJobSubscriberProfileRequestDto request, CancellationToken cancellationToken)
        => await UpsertProfile(request, cancellationToken);

    private async Task<IActionResult> UpsertProfile(UpsertJobSubscriberProfileRequestDto request, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null) return Unauthenticated<JobSubscriberProfileDto>();
        return ToActionResult(await _service.UpsertProfileAsync(_currentUser.UserId.Value, request, cancellationToken));
    }

    [HttpPost("me/cv")]
    [Authorize(Policy = AuthorizationPolicies.RequireJobSubscriber)]
    [RequestSizeLimit(JobDocumentRules.MaxDocumentSizeBytes)]
    public async Task<IActionResult> UploadCv([FromForm] IFormFile? file, CancellationToken cancellationToken)
        => await UploadDocument(JobSubscriberDocumentType.Cv, file, cancellationToken);

    [HttpPost("me/cover-letter")]
    [Authorize(Policy = AuthorizationPolicies.RequireJobSubscriber)]
    [RequestSizeLimit(JobDocumentRules.MaxDocumentSizeBytes)]
    public async Task<IActionResult> UploadCoverLetter([FromForm] IFormFile? file, CancellationToken cancellationToken)
        => await UploadDocument(JobSubscriberDocumentType.CoverLetter, file, cancellationToken);

    private async Task<IActionResult> UploadDocument(JobSubscriberDocumentType documentType, IFormFile? file, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null) return Unauthenticated<JobSubscriberDocumentDto>();

        if (file is null || file.Length == 0)
            return ToActionResult(Result<JobSubscriberDocumentDto>.Failure(ErrorCodes.BAD_REQUEST, "A file is required."));

        await using var stream = file.OpenReadStream();
        var result = await _service.UploadDocumentAsync(_currentUser.UserId.Value, documentType, stream, file.FileName, file.ContentType, file.Length, cancellationToken);
        return ToActionResult(result);
    }

    // Subscribers can re-download their own documents. Streamed through
    // the API — the R2 objects are private and are never linked directly.
    [HttpGet("me/cv")]
    [Authorize(Policy = AuthorizationPolicies.RequireJobSubscriber)]
    public async Task<IActionResult> DownloadOwnCv(CancellationToken cancellationToken)
        => await DownloadOwnDocument(JobSubscriberDocumentType.Cv, cancellationToken);

    [HttpGet("me/cover-letter")]
    [Authorize(Policy = AuthorizationPolicies.RequireJobSubscriber)]
    public async Task<IActionResult> DownloadOwnCoverLetter(CancellationToken cancellationToken)
        => await DownloadOwnDocument(JobSubscriberDocumentType.CoverLetter, cancellationToken);

    private async Task<IActionResult> DownloadOwnDocument(JobSubscriberDocumentType documentType, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null) return Unauthenticated<object>();

        var result = await _service.DownloadOwnDocumentAsync(_currentUser.UserId.Value, documentType, cancellationToken);
        if (!result.IsSuccess) return ToActionResult(result);

        return File(result.Data!.Content, result.Data.ContentType, result.Data.FileName);
    }

    [HttpGet("me/alerts")]
    [Authorize(Policy = AuthorizationPolicies.RequireJobSubscriber)]
    public async Task<IActionResult> GetAlerts(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null) return Unauthenticated<JobAlertPreferenceDto>();
        return ToActionResult(await _service.GetAlertPreferenceAsync(_currentUser.UserId.Value, cancellationToken));
    }

    [HttpPut("me/alerts")]
    [Authorize(Policy = AuthorizationPolicies.RequireJobSubscriber)]
    public async Task<IActionResult> PutAlerts([FromBody] UpdateJobAlertPreferenceRequestDto request, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is null) return Unauthenticated<JobAlertPreferenceDto>();
        return ToActionResult(await _service.UpdateAlertPreferenceAsync(_currentUser.UserId.Value, request, cancellationToken));
    }

    // One-click unsubscribe from an alert email — no session required.
    [HttpPost("unsubscribe")]
    [AllowAnonymous]
    public async Task<IActionResult> Unsubscribe([FromQuery] string token, CancellationToken cancellationToken)
        => ToActionResult(await _service.UnsubscribeByTokenAsync(token, cancellationToken));

    private IActionResult Unauthenticated<T>()
        => ToActionResult(Result<T>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));
}

using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Storage;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs;

public interface IJobSubscriberService
{
    // Enrolment. `currentUserId` is null for an anonymous caller.
    //
    //   • Signed in  → adds the JobSubscriber role to THAT user. No new
    //                  user is ever created, no duplicate-email error.
    //   • Anonymous  → creates a new user, or (when the email already
    //                  exists AND the supplied password matches) adds the
    //                  role to the existing account.
    //
    // Returns a token so the client can continue straight into the
    // profile wizard without a second sign-in round trip.
    Task<Result<AuthTokenDto>> RegisterAsync(RegisterJobSubscriberRequestDto request, Guid? currentUserId, CancellationToken cancellationToken = default);

    Task<Result<JobSubscriberMeDto>> GetMeAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Result<JobSubscriberProfileDto>> UpsertProfileAsync(Guid userId, UpsertJobSubscriberProfileRequestDto request, CancellationToken cancellationToken = default);

    // Document upload. The caller hands over an already-open stream plus
    // the client-declared metadata; the service validates the extension/
    // content type/size before anything is written to storage.
    Task<Result<JobSubscriberDocumentDto>> UploadDocumentAsync(Guid userId, JobSubscriberDocumentType documentType, Stream content, string fileName, string? contentType,
        long sizeBytes, CancellationToken cancellationToken = default);

    // Streams the caller's OWN document. Admin reads go through
    // IAdminJobSubscriberService so the two authorisation paths stay
    // visibly separate.
    Task<Result<FileDownloadResultDto>> DownloadOwnDocumentAsync(Guid userId, JobSubscriberDocumentType documentType, CancellationToken cancellationToken = default);

    Task<Result<JobAlertPreferenceDto>> GetAlertPreferenceAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Result<JobAlertPreferenceDto>> UpdateAlertPreferenceAsync(Guid userId, UpdateJobAlertPreferenceRequestDto request, CancellationToken cancellationToken = default);

    // One-click unsubscribe from an alert email. Token-based so it works
    // without a session.
    Task<Result> UnsubscribeByTokenAsync(string token, CancellationToken cancellationToken = default);
}

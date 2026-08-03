using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Common.Storage;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs;

// Admin read surface over job subscribers. Deliberately read-only for
// documents: an admin can preview/download a CV, but this service
// exposes no way to replace or delete one — that stays with the
// subscriber.
public interface IAdminJobSubscriberService
{
    Task<Result<PagedResult<AdminJobSubscriberListItemDto>>> SearchAsync(AdminJobSubscriberFilterRequestDto filter, CancellationToken cancellationToken = default);
    Task<Result<AdminJobSubscriberDetailDto>> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    // Streams the document through the API. Every call is audited with
    // the acting admin's id — CVs are personal data.
    Task<Result<FileDownloadResultDto>> DownloadDocumentAsync(Guid userId, JobSubscriberDocumentType documentType, CancellationToken cancellationToken = default);

    // Short-lived pre-signed URL for in-browser preview. Same audit
    // trail as the streamed download.
    Task<Result<string>> GetDocumentPreviewUrlAsync(Guid userId, JobSubscriberDocumentType documentType, CancellationToken cancellationToken = default);
}

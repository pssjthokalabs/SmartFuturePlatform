using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Auditing;

public interface IAuditService
{
    Task LogAsync(CreateAuditLogRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<AuditLogDto>>> SearchAsync(AuditLogFilterRequestDto filter);
}

using SmartFuture.Application.Admin.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Admin;

public interface IAdminSystemService
{
    Task<Result<SystemSummaryDto>> GetSystemSummaryAsync(CancellationToken cancellationToken = default);
}

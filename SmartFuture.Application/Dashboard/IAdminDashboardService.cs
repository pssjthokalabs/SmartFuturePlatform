using SmartFuture.Application.Dashboard.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Dashboard;

public interface IAdminDashboardService
{
    Task<Result<AdminDashboardSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken = default);
}

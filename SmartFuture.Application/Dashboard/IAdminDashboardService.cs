using SmartFuture.Application.Dashboard.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Dashboard;

public interface IAdminDashboardService
{
    Task<Result<AdminDashboardSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Slim sidebar-badges payload. Three counts each for services
    /// and orders. Cheap on purpose — only COUNT aggregations, no row
    /// projections — so the admin sidebar can poll it on a 60–120s
    /// interval without piling on the database.
    /// </summary>
    Task<Result<AdminSidebarCountsDto>> GetSidebarCountsAsync(CancellationToken cancellationToken = default);
}

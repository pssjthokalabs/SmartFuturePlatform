using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

public interface IBillingOverviewService
{
    Task<Result<BillingOverviewDto>> GetMineAsync(CancellationToken cancellationToken = default);
}

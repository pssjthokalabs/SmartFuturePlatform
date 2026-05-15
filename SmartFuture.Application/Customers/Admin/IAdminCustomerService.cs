using SmartFuture.Application.Customers.Admin.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Customers.Admin;

public interface IAdminCustomerService
{
    Task<Result<List<AdminCustomerListItemDto>>> SearchAsync(AdminCustomerFilterRequestDto filter, CancellationToken cancellationToken = default);
    Task<Result<AdminCustomerDetailDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<AdminCustomerDetailDto>> UpdateAsync(Guid id, UpdateAdminCustomerRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<AdminCustomerDetailDto>> ActivateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<AdminCustomerDetailDto>> SuspendAsync(Guid id, CancellationToken cancellationToken = default);
}

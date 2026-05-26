using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Orders;

public interface IOrderService
{
    Task<Result<PagedResult<OrderDto>>> SearchAdminAsync(
        OrderFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<OrderDto>>> GetMineAsync(
        OrderFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<OrderDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> CreateMineAsync(
        CreateOrderRequestDto request, CancellationToken cancellationToken = default);

    // Phase 51 — eligibility probe for the customer order wizard.
    // Returns whether the current user is allowed to create a new
    // order right now; if not, surfaces the blocking order so the
    // mobile/portal UI can deep-link to it.
    Task<Result<CustomerOrderEligibilityDto>> GetMyEligibilityAsync(
        CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> AdminUpdateAsync(
        Guid id, AdminUpdateOrderRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> AdminUpdateStatusAsync(
        Guid id, AdminUpdateOrderStatusDto request, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> AdminSetInstallationDateAsync(
        Guid id, AdminSetOrderInstallationDateDto request, CancellationToken cancellationToken = default);

    Task<Result> CancelMineAsync(
        Guid id, string? cancellationReason = null, CancellationToken cancellationToken = default);

    // Phase 51 — customer-initiated install-address change. Validates
    // ownership, status, and re-runs coverage on the new lat/lng
    // before persisting. Returns the updated order on success.
    Task<Result<OrderDto>> RequestAddressChangeMineAsync(
        Guid id, RequestAddressChangeRequestDto request, CancellationToken cancellationToken = default);
}

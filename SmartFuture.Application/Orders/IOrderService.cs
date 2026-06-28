using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Shared.Enums.ServicePackages;
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
    // mobile/portal UI can deep-link to it. Pass `requestedType` to
    // probe per product line — a Fibre eligibility check ignores
    // existing Security orders and vice versa. Omitting the parameter
    // falls back to the legacy "any open order blocks" semantics for
    // backwards-compatible callers.
    Task<Result<CustomerOrderEligibilityDto>> GetMyEligibilityAsync(
        ServicePackageType? requestedType = null,
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

    // Go-live alignment — admin "Mark service activated on Openserve".
    // Only callable after the customer has paid the first monthly
    // invoice (Order.Status = PendingActivation). Records the
    // activation date (default now) as the billing anchor, sets
    // NextPayDateUtc = activation + 30 days, and flips the order to
    // Active. The whole point is that Openserve activation is manual,
    // so this is the only path to OrderStatus.Active for service
    // accounts.
    Task<Result<OrderDto>> AdminActivateServiceAsync(
        Guid id, AdminActivateServiceRequestDto request, CancellationToken cancellationToken = default);
}

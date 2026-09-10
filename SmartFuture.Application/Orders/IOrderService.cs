using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Orders;

public interface IOrderService
{
    /// <summary>
    /// Result.Message returned by TryOpenserveConfirmedActivateServiceAsync
    /// only when activation genuinely just happened on this call — used
    /// by OpenserveOrderUpdatePipeline to decide whether to fire the
    /// "service activated" customer notification exactly once (not on
    /// an idempotent-no-op replay where the order was already Active).
    /// </summary>
    public const string OpenserveActivationSuccessMessage = "Service activated (Openserve-confirmed).";


    Task<Result<PagedResult<OrderDto>>> SearchAdminAsync(
        OrderFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<OrderDto>>> GetMineAsync(
        OrderFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<OrderDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> CreateMineAsync(
        CreateOrderRequestDto request, CancellationToken cancellationToken = default);

    // Free-activation order placement. For packages whose activation
    // once-off fee is waived (ServicePackage.HasFreeInstallation == true):
    // no payment gateway, no invoice, no payment row. Creates the Order +
    // pending NetworkAccount directly — the same activation handoff a paid
    // order reaches after its payment settles. Rejects non-free packages
    // with CONFLICT so the client falls back to normal paid checkout.
    Task<Result<OrderDto>> CreateFreeActivationMineAsync(
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

    // Openserve-confirmed activation (brief Priority 9). Called by the
    // Openserve status-update pipeline when an order's Openserve
    // lifecycle reaches the documented terminal "Accepted" state. Runs
    // the exact same core activation steps as AdminActivateServiceAsync
    // (idempotent, same PendingActivation precondition, same pro-rata
    // invoice guard) via a shared private core — never a second,
    // divergent implementation. Returns success-with-no-op (not a
    // failure) when the order isn't in PendingActivation yet: Openserve
    // completing does not override SmartFuture's own billing
    // prerequisites, so the event is simply not actionable until the
    // customer has also paid.
    Task<Result<OrderDto>> TryOpenserveConfirmedActivateServiceAsync(
        Guid orderId, string? openserveActivationReference, CancellationToken cancellationToken = default);
}

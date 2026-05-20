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

    Task<Result<OrderDto>> AdminUpdateAsync(
        Guid id, AdminUpdateOrderRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> AdminUpdateStatusAsync(
        Guid id, AdminUpdateOrderStatusDto request, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> AdminSetInstallationDateAsync(
        Guid id, AdminSetOrderInstallationDateDto request, CancellationToken cancellationToken = default);

    Task<Result> CancelMineAsync(
        Guid id, string? cancellationReason = null, CancellationToken cancellationToken = default);
}

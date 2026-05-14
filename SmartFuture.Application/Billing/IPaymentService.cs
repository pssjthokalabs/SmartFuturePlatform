using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

public interface IPaymentService
{
    Task<Result<PagedResult<PaymentDto>>> SearchAdminAsync(
        PaymentFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<PaymentDto>>> GetMineAsync(
        PaymentFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PaymentDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<PaymentDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PaymentDto>> CreateAsync(
        CreatePaymentRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<PaymentDto>> AdminUpdateStatusAsync(
        Guid id, AdminUpdatePaymentStatusDto request, CancellationToken cancellationToken = default);
}

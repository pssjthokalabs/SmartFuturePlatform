using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments;

public interface IPaymentApplierService
{
    Task<Result<PaymentDto>> ApplyStatusChangeAsync(
        ApplyPaymentStatusChangeRequestDto request,
        CancellationToken cancellationToken = default);
}

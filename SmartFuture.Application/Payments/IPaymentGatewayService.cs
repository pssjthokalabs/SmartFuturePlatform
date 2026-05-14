using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments;

public interface IPaymentGatewayService
{
    Task<Result<InitiateInvoicePaymentResultDto>> InitiateInvoicePaymentAsync(
        InitiateInvoicePaymentRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<PaymentInitiationDto>>> SearchAdminAsync(
        PaymentInitiationFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PaymentInitiationDto>> GetAdminByIdAsync(
        Guid id, CancellationToken cancellationToken = default);
}

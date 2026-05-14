using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

public interface IInvoiceService
{
    Task<Result<PagedResult<InvoiceDto>>> SearchAdminAsync(
        InvoiceFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<InvoiceDto>>> GetMineAsync(
        InvoiceFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<InvoiceDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> CreateAsync(
        CreateInvoiceRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> AdminUpdateAsync(
        Guid id, AdminUpdateInvoiceRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> AdminUpdateStatusAsync(
        Guid id, AdminUpdateInvoiceStatusDto request, CancellationToken cancellationToken = default);
}

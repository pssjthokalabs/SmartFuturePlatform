using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

public interface IDebitOrderMandateService
{
    Task<Result<PagedResult<DebitOrderMandateDto>>> SearchAdminAsync(
        DebitOrderMandateFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<DebitOrderMandateDto>>> GetMineAsync(
        DebitOrderMandateFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<DebitOrderMandateDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<DebitOrderMandateDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<DebitOrderMandateDto>> CreateMineAsync(
        CreateDebitOrderMandateRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<DebitOrderMandateDto>> AdminUpdateAsync(
        Guid id, AdminUpdateDebitOrderMandateRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<DebitOrderMandateDto>> AdminUpdateStatusAsync(
        Guid id, AdminUpdateDebitOrderMandateStatusDto request, CancellationToken cancellationToken = default);
}

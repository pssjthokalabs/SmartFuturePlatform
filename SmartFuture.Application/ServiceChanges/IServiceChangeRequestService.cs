using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.ServiceChanges.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.ServiceChanges;

public interface IServiceChangeRequestService
{
    // Customer endpoints
    Task<Result<PagedResult<ServiceChangeRequestDto>>> GetMineAsync(
        ServiceChangeRequestFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<ServiceChangeRequestDto>> GetMineByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    /// <summary>No DB writes; returns server-calculated pro-rata + a
    /// display summary the wizard renders verbatim.</summary>
    Task<Result<ServiceChangePreviewDto>> PreviewMineAsync(
        PreviewServiceChangeRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create the request. Upgrades emit a pro-rata Invoice (+
    /// optional mock Payment via UAT checkout) and set status
    /// PendingPayment. Downgrades set status Scheduled with
    /// EffectiveDateUtc on the next billing cycle anchor.
    /// </summary>
    Task<Result<ServiceChangeRequestDto>> CreateMineAsync(
        CreateServiceChangeRequestDto request, CancellationToken cancellationToken = default);

    Task<Result> CancelMineAsync(
        Guid id, string? cancellationReason = null, CancellationToken cancellationToken = default);

    // Admin endpoints
    Task<Result<PagedResult<ServiceChangeRequestDto>>> SearchAdminAsync(
        ServiceChangeRequestFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<ServiceChangeRequestDto>> GetAdminByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply the package swap on the underlying NetworkAccount and mark
    /// the request Completed. Used by admins to push through scheduled
    /// downgrades on/after EffectiveDate, or to manually finalise an
    /// upgrade when the auto-hook didn't fire.
    /// </summary>
    Task<Result<ServiceChangeRequestDto>> AdminProcessAsync(
        Guid id, AdminProcessServiceChangeRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<ServiceChangeRequestDto>> AdminRejectAsync(
        Guid id, AdminRejectServiceChangeRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<ServiceChangeRequestDto>> AdminCancelAsync(
        Guid id, AdminCancelServiceChangeRequestDto request, CancellationToken cancellationToken = default);

    // Internal hook — invoked by PaymentService when a pro-rata
    // Invoice's Payment becomes Completed. Best-effort, idempotent:
    // no-ops when the matching request isn't in PendingPayment.
    Task<Result> OnInvoicePaidAsync(
        Guid invoiceId, Guid? paymentId, CancellationToken cancellationToken = default);
}

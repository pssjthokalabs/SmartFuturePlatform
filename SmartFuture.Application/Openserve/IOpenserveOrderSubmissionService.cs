using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

public interface IOpenserveOrderSubmissionService
{
    /// <summary>
    /// Best-effort, idempotent submission trigger. Called from
    /// NetworkAccountService whenever a NetworkAccount is reserved/
    /// created for a Fibre order (the "reached correct paid/eligible
    /// state" point, per brief §Priority-1) — never throws, callers
    /// treat it as fire-and-forget. Safe to call more than once for the
    /// same order: a second call is a no-op once an OpenserveOrder row
    /// already exists (in any state — automatic re-triggering never
    /// re-attempts a Failed submission; that requires
    /// <see cref="AdminRetrySubmissionAsync"/>).
    /// </summary>
    Task<Result> TrySubmitForOrderAsync(Guid orderId, Guid networkAccountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin-triggered retry of a Failed (or stuck Submitting)
    /// submission. Reuses the SAME ExternalReferenceNumber and
    /// SubscriberReferenceNumber already persisted — never regenerates
    /// them — and mints a fresh MessageID per spec.
    /// </summary>
    Task<Result<OpenserveOrderDto>> AdminRetrySubmissionAsync(Guid openserveOrderId, CancellationToken cancellationToken = default);

    Task<Result<OpenserveOrderDto>> GetByIdAsync(Guid openserveOrderId, CancellationToken cancellationToken = default);

    Task<Result<OpenserveOrderDto>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<OpenserveOrderDto>>> SearchAdminAsync(OpenserveOrderFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<OpenserveOrderStatusHistoryDto>>> GetHistoryAsync(Guid openserveOrderId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<OpenserveIntegrationLogDto>>> GetIntegrationLogsAsync(Guid openserveOrderId, CancellationToken cancellationToken = default);
}

public class OpenserveOrderFilterRequestDto
{
    public string? Search { get; set; }
    public Shared.Enums.Openserve.OpenserveProvisioningStatus? Status { get; set; }
    public bool? IsTerminal { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

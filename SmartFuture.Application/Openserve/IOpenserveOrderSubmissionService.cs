using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

public interface IOpenserveOrderSubmissionService
{
    /// <summary>
    /// The single submission coordinator. Every path — the automatic trigger,
    /// Admin Send/Retry, the recovery worker and the safety sweep — calls this.
    /// It claims the order's submission record atomically, re-validates
    /// everything (kill switch, order state, Admin pause, mapping, AMID,
    /// configuration), then sends at most one Create Order request. It never
    /// sends again once Openserve accepted the order, and never resends an
    /// attempt whose outcome is unknown without Admin confirmation.
    /// Success = an attempt ran (Data.Outcome says Submitted / Failed / Blocked);
    /// Failure = refused before anything was written (code CONFLICT /
    /// VALIDATION_ERROR / NOT_FOUND, with the reason in Message).
    /// </summary>
    Task<Result<OpenserveSubmissionAttemptDto>> SubmitAsync(OpenserveSubmissionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The automatic business trigger, called from NetworkAccountService when a
    /// Fibre order's network account is reserved/created (payment landed).
    /// Fire-and-forget: never throws, and "nothing to do" (disabled, not Fibre,
    /// already handled, paused) returns success. Delegates to <see cref="SubmitAsync"/>.
    /// </summary>
    Task<Result> TrySubmitForOrderAsync(Guid orderId, Guid networkAccountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin retry by OpenserveOrder id (Integrations console). Delegates to
    /// <see cref="SubmitAsync"/> — reuses the SAME ExternalReferenceNumber and
    /// SubscriberReferenceNumber, mints a fresh MessageID.
    /// </summary>
    Task<Result<OpenserveOrderDto>> AdminRetrySubmissionAsync(Guid openserveOrderId, bool confirmOutcomeUnknown = false, CancellationToken cancellationToken = default);

    /// <summary>Records claims that went stale (attempt interrupted mid-call) as OutcomeUnknown. Returns how many were resolved.</summary>
    Task<int> ResolveStaleSubmissionsAsync(CancellationToken cancellationToken = default);

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

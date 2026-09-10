using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

public interface IOpenserveReconciliationService
{
    /// <summary>
    /// GET the current Openserve order state and feed it into the SAME
    /// update pipeline webhooks use (brief Priority 5: "webhook and
    /// poll use same update pipeline"). Used by both the background
    /// worker and the admin "Synchronize now" action — one
    /// implementation, two callers.
    /// </summary>
    Task<Result> SynchronizeNowAsync(Guid openserveOrderId, CancellationToken cancellationToken = default);

    /// <summary>Runs SynchronizeNowAsync sequentially over every non-terminal, previously-submitted OpenserveOrder. Called by the hosted service on each tick.</summary>
    Task<int> ReconcileNonTerminalOrdersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// POST {BaseUrl}/{ws-ispcode}/cancelproductorder (spec §4.7) —
    /// admin-triggered only, and only while the order isn't already
    /// terminal ("used to cancel an in progress or inflight product
    /// order that is not yet completed"). Feeds the result through the
    /// same update pipeline as everything else.
    /// </summary>
    Task<Result> AdminCancelOrderAsync(Guid openserveOrderId, CancellationToken cancellationToken = default);
}

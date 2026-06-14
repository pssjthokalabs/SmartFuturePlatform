namespace SmartFuture.Shared.Enums.Payments;

public enum PaymentInitiationStatus
{
    Created = 0,
    RedirectRequired = 1,
    Pending = 2,
    Failed = 3,
    Cancelled = 4,

    /// <summary>
    /// Phase 1C2 — terminal success. Set by the auto-billing settlement
    /// reconciler when an async provider (PayFast) ITN confirms COMPLETE,
    /// so a settled initiation no longer reads as Pending. Int-backed enum;
    /// no DB check constraint on PaymentInitiation.Status, so additive.
    /// </summary>
    Succeeded = 5
}

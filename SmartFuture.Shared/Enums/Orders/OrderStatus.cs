namespace SmartFuture.Shared.Enums.Orders;

public enum OrderStatus
{
    Draft = 0,
    Submitted = 1,
    Confirmed = 2,
    AwaitingPayment = 3,
    PaymentReceived = 4,
    Provisioning = 5,
    Active = 6,
    Cancelled = 7,
    Failed = 8,
    Rejected = 9,

    // ─── Post-installation lifecycle (added go-live alignment) ──────
    //
    // Openserve does not yet provide an API for automatic line
    // activation, so service activation is a deliberate admin step.
    // The two new statuses model the gap between
    // "technician finished" and "line is live":
    //
    //   PendingPayment      — installation Completed, but the
    //                         first monthly invoice has not been
    //                         paid yet (auto-debit failed, customer
    //                         opted out, mandate missing, etc.).
    //   PendingActivation   — first monthly invoice IS paid; admin
    //                         must still activate the line manually
    //                         on Openserve before service goes Active.
    //
    // The state machine is:
    //   ... → Provisioning → PendingPayment ⇄ PendingActivation → Active
    //
    // Admin "Mark service activated on Openserve" is the only path
    // out of PendingActivation. Never automatically.
    PendingPayment = 10,
    PendingActivation = 11
}

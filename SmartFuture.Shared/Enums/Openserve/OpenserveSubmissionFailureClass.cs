namespace SmartFuture.Shared.Enums.Openserve;

// Why the last Create Order (POST /productorder) attempt for an
// OpenserveOrder did not succeed — persisted so the recovery worker and
// Admin both know whether it is safe to send again.
//
// Openserve has no idempotency key and no lookup by our External
// Reference Number, and its callbacks/events only carry Openserve's own
// order id. So a request that MAY have reached Openserve (timeout,
// dropped connection, 500/502/504, unreadable 2xx) can't be checked from
// our side: resending it could create a second Openserve order. Those
// are OutcomeUnknown and are never resent automatically.
public enum OpenserveSubmissionFailureClass
{
    /// <summary>No failure recorded — never attempted, or the last attempt succeeded.</summary>
    None = 0,

    /// <summary>Definitely not processed by Openserve (could not connect, 429, 503, 408) — resent automatically with backoff.</summary>
    Retryable = 1,

    /// <summary>Openserve (or its gateway) answered and refused the request (4xx, auth, business rejection) — fix the cause, then retry manually.</summary>
    NonRetryable = 2,

    /// <summary>Never sent — a SmartFuture-side precondition failed (mapping, AMID, address, contact, configuration).</summary>
    Blocked = 3,

    /// <summary>The request may have reached Openserve — never resent automatically; a manual retry needs explicit confirmation.</summary>
    OutcomeUnknown = 4
}

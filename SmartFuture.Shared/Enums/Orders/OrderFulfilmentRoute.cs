namespace SmartFuture.Shared.Enums.Orders;

// Who fulfils a Fibre order — decided server-side when the order (or the
// payment-first OrderIntent) is created, and persisted so every later
// step reads the same answer instead of re-deriving it.
//
// The Openserve submission service sends an order ONLY when this is
// Openserve. "Fibre package" or "paid" alone is never proof.
public enum OrderFulfilmentRoute
{
    // Created before the route was recorded (or not a Fibre order). Never
    // sent to Openserve automatically; an Admin may send it explicitly,
    // which records Openserve.
    NotRecorded = 0,

    // Qualified through Openserve (authenticated Product Qualification,
    // or the public GIS lookup while the integration is off). Eligible for
    // automatic Openserve submission after payment.
    Openserve = 1,

    // Qualified through SmartFuture's own coverage inclusion list (an
    // active Coverage Map Include rule). Fulfilled by SmartFuture — never
    // sent to Openserve by any path, automatic or manual.
    InternalInclusion = 2
}

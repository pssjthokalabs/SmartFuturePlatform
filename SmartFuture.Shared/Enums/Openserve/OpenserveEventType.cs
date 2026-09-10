namespace SmartFuture.Shared.Enums.Openserve;

// Mirrors the four documented Product Order Notification event types
// (Openserve Fulfilment API Spec ITSD-179559 Rev 04.002 §8) plus an
// Unknown catch-all so a future/undocumented eventType never fails
// deserialization — it still gets stored, just unmapped.
public enum OpenserveEventType
{
    Unknown = 0,
    ProductOrderCreateEvent = 1,
    ProductOrderStateChangeEvent = 2,
    CancelProductOrderCreateEvent = 3,
    CancelProductOrderStateChangeEvent = 4
}

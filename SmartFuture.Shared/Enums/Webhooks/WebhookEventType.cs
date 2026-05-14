namespace SmartFuture.Shared.Enums.Webhooks;

public enum WebhookEventType
{
    Unknown = 0,
    PaymentPending = 1,
    PaymentCompleted = 2,
    PaymentFailed = 3,
    PaymentCancelled = 4,
    PaymentRefunded = 5,
    PaymentReversed = 6,
    MandateCreated = 7,
    MandateActivated = 8,
    MandateCancelled = 9,
    Other = 99
}

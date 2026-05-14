namespace SmartFuture.Shared.Enums.Webhooks;

public enum WebhookInboxStatus
{
    Received = 0,
    SignatureInvalid = 1,
    Duplicate = 2,
    Processed = 3,
    Failed = 4,
    Ignored = 5
}

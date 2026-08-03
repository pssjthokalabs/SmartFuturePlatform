namespace SmartFuture.Shared.Enums.Jobs;

public enum JobAlertDeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    // Nothing matched the subscriber's preferences for the window, or the
    // module-level JobAlertsEnabled switch was off. Logged (not silently
    // dropped) so "why didn't I get an alert" is answerable.
    Skipped = 3
}

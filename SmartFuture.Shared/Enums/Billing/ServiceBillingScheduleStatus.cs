namespace SmartFuture.Shared.Enums.Billing;

/// <summary>
/// Lifecycle of a <c>ServiceBillingSchedule</c> — the per-service
/// recurring-billing anchor introduced in Phase 0A. Only <see cref="Active"/>
/// schedules will be picked up by the (future) recurring invoice generator.
/// Phase 0A creates the type but nothing reads it yet.
/// </summary>
public enum ServiceBillingScheduleStatus
{
    Active = 0,
    Paused = 1,
    Cancelled = 2
}

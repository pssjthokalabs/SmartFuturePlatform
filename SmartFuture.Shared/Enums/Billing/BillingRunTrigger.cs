namespace SmartFuture.Shared.Enums.Billing;

/// <summary>
/// What initiated a recurring-billing run. Phase 0A only ever produces
/// <see cref="Scheduler"/> runs (from the hosted service);
/// <see cref="AdminManual"/> is reserved for a future opt-in trigger and
/// is intentionally NOT wired to any production charge path in Phase 0A.
/// </summary>
public enum BillingRunTrigger
{
    Scheduler = 0,
    AdminManual = 1
}

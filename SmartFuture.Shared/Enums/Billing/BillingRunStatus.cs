namespace SmartFuture.Shared.Enums.Billing;

/// <summary>
/// State of a single recurring-billing run (one <c>BillingRunLog</c> row).
/// The <see cref="Running"/> row also acts as the DB-backed concurrency
/// lock — only one non-stale Running row is permitted at a time.
/// </summary>
public enum BillingRunStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2
}

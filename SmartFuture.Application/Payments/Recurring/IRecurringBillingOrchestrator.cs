namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0A — orchestrates a single recurring-billing run: acquires the
/// DB-backed run lock, runs the (currently no-op) stages, and finalizes
/// the <c>BillingRunLog</c> row.
///
/// Stages in Phase 0A are NO-OP:
///   1. invoice generation — no-op (Phase 0B)
///   2. due-invoice charge  — no-op (Phase 0C)
///   3. retry runner        — no-op (Phase 0D)
///   4. grace/suspension    — no-op (Phase 0E)
/// </summary>
public interface IRecurringBillingOrchestrator
{
    Task<RecurringBillingRunSummary> RunAsync(
        RecurringBillingRunContext context,
        CancellationToken cancellationToken = default);
}

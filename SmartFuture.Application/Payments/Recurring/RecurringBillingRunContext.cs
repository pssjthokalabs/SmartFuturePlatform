using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Inputs for a single recurring-billing run. Built by the caller
/// (the hosted service) from <c>AutoBillingSettings</c> + the current time.
/// </summary>
public sealed record RecurringBillingRunContext(
    Guid RunId,
    DateTime NowUtc,
    bool DryRun,
    BillingRunTrigger TriggeredBy,
    int MaxInvoicesPerRun,
    int MaxChargesPerRun);

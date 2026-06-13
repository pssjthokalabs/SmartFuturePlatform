namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0B — owns the lifecycle of <c>ServiceBillingSchedule</c> rows
/// (the per-service recurring-billing anchor).
/// </summary>
public interface IServiceBillingScheduleService
{
    /// <summary>
    /// Idempotently create + activate the recurring billing schedule for a
    /// service whose FIRST service-fee invoice has just been paid. The
    /// recurring 30-day cycle anchors off <paramref name="paidAtUtc"/> (the
    /// payment date), per the business rule. A no-op if the invoice is not a
    /// service-fee invoice, if the order's network account doesn't exist, or
    /// if a schedule already exists for that order (so recurring-invoice
    /// payments never re-anchor the cycle).
    ///
    /// Best-effort: never throws to the caller — failures are logged. Phase
    /// 0B does NOT charge, retry, or suspend here; it only sets the anchor.
    /// </summary>
    Task EnsureActivatedForPaidServiceInvoiceAsync(
        Guid invoiceId, DateTime paidAtUtc, CancellationToken cancellationToken = default);
}

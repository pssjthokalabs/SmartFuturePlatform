namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0F-notify — the NEW deduped billing notifications (recurring
/// invoice generated, grace/suspension candidate, internal ops alert). All
/// sends are gated by <c>AutoBillingSettings</c> flags (default OFF),
/// deduped against <c>OutboundNotification</c>, and best-effort (never throw,
/// never block billing). Success/failure/final emails are NOT here — those
/// stay in the applier (InvoicePaid) + AutoBillingEmailService.
///
/// No token / card / PAN / passphrase / signature / raw payload is ever
/// included in any email, log, or stored notification row.
/// </summary>
public interface IBillingNotificationService
{
    /// <summary>Customer email that a recurring invoice was generated. Deduped per invoice.</summary>
    Task NotifyInvoiceGeneratedAsync(
        Guid invoiceId, string invoiceNumber, Guid userId,
        decimal amount, DateTime? dueAtUtc, string currencyCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grace/suspension-candidate notifications: a polite customer warning
    /// (no "suspended" claim) and/or an internal ops alert, each gated +
    /// deduped per invoice independently.
    /// </summary>
    Task NotifyGraceCandidateAsync(
        Guid invoiceId, string invoiceNumber, Guid userId,
        decimal balanceDue, double daysOverdue,
        CancellationToken cancellationToken = default);
}

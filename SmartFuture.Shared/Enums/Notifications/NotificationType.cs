namespace SmartFuture.Shared.Enums.Notifications;

public enum NotificationType
{
    Welcome = 0,
    EmailVerification = 1,
    PasswordReset = 2,
    OrderCreated = 3,
    OrderStatusChanged = 4,
    InstallationScheduled = 5,
    InstallationStatusChanged = 6,
    InvoiceIssued = 7,
    InvoicePaid = 8,
    PaymentStatusChanged = 9,
    DebitOrderStatusChanged = 10,
    SupportTicketCreated = 11,
    SupportTicketCommentAdded = 12,
    SupportTicketStatusChanged = 13,
    AdminNotice = 14,
    SystemNotice = 15,

    // ─── Phase 0F-notify — recurring billing notifications ─────────
    // Additive int-backed values (no DB check constraint on
    // OutboundNotification.Type). Used as the dedupe discriminator with
    // (RelatedEntityType, RelatedEntityId).
    RecurringInvoiceGenerated = 16,
    BillingGraceCandidate = 17,
    BillingInternalAlert = 18,

    // Day-N-after-due reminder. Emitted by the (future) overdue-
    // reminder pipeline separately from BillingGraceCandidate — an
    // invoice can be overdue for the entire grace window and only
    // becomes a "grace candidate" once the window elapses. Deduped
    // via (Invoice, DayNumber) in the caller so a five-day-overdue
    // invoice doesn't spam five identical emails.
    InvoiceOverdue = 19,

    // Job Opportunities alert newsletter digest. Additive int-backed
    // value; existing OutboundNotification rows are unaffected.
    JobAlertDigest = 20,

    // ─── Openserve fulfilment integration ──────────────────────────
    // Meaningful, customer-facing Openserve order/install transitions
    // only (brief Priority 6) — not every raw Openserve status change.
    OpenserveOrderReceived = 21,
    OpenserveOrderProcessing = 22,
    OpenserveInstallationScheduled = 23,
    OpenserveActionRequired = 24,
    OpenserveInstallationComplete = 25,
    OpenserveServiceActivated = 26,
    OpenserveOrderFailed = 27,
    OpenserveOrderCancelled = 28
}

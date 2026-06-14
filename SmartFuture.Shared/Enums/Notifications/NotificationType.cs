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
    BillingInternalAlert = 18
}

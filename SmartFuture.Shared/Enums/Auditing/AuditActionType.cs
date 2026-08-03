namespace SmartFuture.Shared.Enums.Auditing;

public enum AuditActionType
{
    UserRegistered = 0,
    UserLoggedIn = 1,
    UserLoggedOut = 2,
    RefreshTokenIssued = 3,
    RefreshTokenRevoked = 4,
    CustomerProfileCreated = 5,
    CustomerProfileUpdated = 6,
    UserStatusChanged = 7,
    UserRoleChanged = 8,
    ServicePackageCreated = 9,
    ServicePackageUpdated = 10,
    ServicePackageActivated = 11,
    ServicePackageDeactivated = 12,
    ServicePackageArchived = 13,
    CoverageRequestCreated = 14,
    CoverageRequestStatusChanged = 15,
    OrderCreated = 16,
    OrderStatusChanged = 17,
    InstallationStatusChanged = 18,
    InvoiceCreated = 19,
    PaymentStatusChanged = 20,
    DebitOrderUpdated = 21,
    SupportTicketCreated = 22,
    SupportTicketStatusChanged = 23,
    AdminAction = 24,
    SystemAction = 25,
    NetworkAccountProvisioned = 26,
    NetworkAccountSuspended = 27,
    NetworkAccountResumed = 28,
    NetworkAccountTerminated = 29,
    NetworkAccountPackageChanged = 30,
    NetworkAccountProvisionFailed = 31,
    PasswordResetRequested = 32,
    PasswordResetCompleted = 33,
    PasswordChangeCodeRequested = 34,
    PasswordChanged = 35,
    PasswordChangeCodeFailed = 36,
    UserCreated = 37,
    PaymentWebhookDryRun = 38,
    CustomerPaymentMandateStored = 39,
    CustomerPaymentMandateUpdated = 40,
    CustomerPaymentMandateRevoked = 41,
    AutoBillingChargeAttempted = 42,
    AutoBillingChargeSucceeded = 43,
    AutoBillingChargeFailed = 44,

    // ─── Billing Ops v1 — manual service invoice creation ──────────
    // Additive int-backed values (no DB check constraint on
    // AuditLog.ActionType). A normal manual create and a force-created
    // schedule-detached duplicate are audited distinctly.
    ManualServiceInvoiceCreated = 45,
    ForcedDuplicateServiceInvoiceCreated = 46,

    // ─── Job Opportunities module ──────────────────────────────────
    // Additive int-backed values; AuditLog.ActionType has no check
    // constraint, so existing rows are unaffected.
    JobOpportunityCreated = 47,
    JobOpportunityUpdated = 48,
    JobOpportunityStatusChanged = 49,
    JobSourceCreated = 50,
    JobSourceUpdated = 51,
    JobSourceDeleted = 59,
    JobImportRunCompleted = 52,
    JobSubscriberEnrolled = 53,
    JobSubscriberProfileUpdated = 54,
    JobSubscriberDocumentUploaded = 55,
    JobSubscriberDocumentAccessed = 56,
    JobSettingsUpdated = 57,
    JobAlertPreferenceUpdated = 58
}

namespace SmartFuture.Shared.Enums.Auditing;

public enum AuditEntityType
{
    User = 0,
    CustomerProfile = 1,
    ServicePackage = 2,
    CoverageRequest = 3,
    Order = 4,
    Installation = 5,
    Invoice = 6,
    Payment = 7,
    DebitOrder = 8,
    SupportTicket = 9,
    Auth = 10,
    System = 11,
    NetworkAccount = 12,
    ServiceChangeRequest = 13,
    PaymentInitiation = 14,
    CustomerPaymentMandate = 15,

    // ─── Job Opportunities module ──────────────────────────────────
    JobOpportunity = 16,
    JobSource = 17,
    JobSubscriberProfile = 18,
    JobSettings = 19,

    // ─── Openserve fulfilment integration ──────────────────────────
    OpenserveOrder = 20,
    PackageOpenserveMapping = 21
}

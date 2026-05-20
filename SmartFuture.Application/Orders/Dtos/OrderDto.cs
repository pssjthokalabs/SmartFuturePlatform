using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.Orders.Dtos;

// Small customer-safe payment summary attached to OrderDto detail
// responses. Resolved from the latest related Invoice + its most
// recent successful Payment so the order detail page can show
// "Method: Ozow / Reference: OZOW-MOCK-… / Status: Completed" plus
// deep-links into /client/billing/{invoices,payments}/:id.
//
// Returned only on detail/create endpoints — the list projection
// would force an N+1 join per row for no UX benefit (list rows show
// status and amount only).
//
// Admin-only fields (Notes, AdminNotes, internal audit) are NOT
// surfaced here — admins can drill into the existing
// /admin/payments/:id and /admin/invoices/:id pages.
public class OrderPaymentSummaryDto
{
    public Guid PaymentId { get; set; }
    public string PaymentNumber { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; }
    public PaymentMethodType Method { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";
    public DateTime? PaidAtUtc { get; set; }

    public string? GatewayName { get; set; }
    public string? GatewayReference { get; set; }

    public Guid? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public InvoiceStatus? InvoiceStatus { get; set; }
}

// Phase 39 — small installation summary attached to OrderDto detail
// responses so the order page can deep-link into the matching
// Installation when the admin has scheduled one. Only the most recent
// non-terminal installation is surfaced; admins can drill into the
// /admin/installations index to see the full history.
public class OrderInstallationSummaryDto
{
    public Guid Id { get; set; }
    public string InstallationNumber { get; set; } = string.Empty;
    public InstallationStatus Status { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public class OrderDto
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public Guid? CustomerProfileId { get; set; }
    public Guid? ServicePackageId { get; set; }
    public Guid? CoverageRequestId { get; set; }

    public OrderStatus Status { get; set; }
    public OrderSource Source { get; set; }

    public string PackageName { get; set; } = string.Empty;
    public ServicePackageType PackageType { get; set; }
    public string? PackageSpeedLabel { get; set; }
    public string? PackageDataAllowanceLabel { get; set; }
    public bool PackageIsUncapped { get; set; }
    public decimal PackagePrice { get; set; }
    public ServicePackageBillingCycle PackageBillingCycle { get; set; }
    public int? PackageContractMonths { get; set; }
    public bool PackageHasFreeInstallation { get; set; }
    public decimal? PackageInstallationFee { get; set; }
    public bool PackageIncludesRouter { get; set; }

    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? GooglePlaceId { get; set; }

    public string? CustomerNotes { get; set; }
    public string? AdminNotes { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }

    // Phase 44 — see Order.RequestedInstallationDateUtc. Always the
    // customer's original ask; never modified by admin actions.
    public DateTime? RequestedInstallationDateUtc { get; set; }
    public DateTime? ExpectedInstallationDateUtc { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public string? LastStatusChangedByUserEmail { get; set; }

    public string? CancellationReason { get; set; }
    public string? FailureReason { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    // Populated on detail/create responses only — null on list rows.
    // See `OrderPaymentSummaryDto` notes above for resolution rules.
    public OrderPaymentSummaryDto? Payment { get; set; }

    // Populated on detail/create/admin-update responses. Null on list
    // rows (the list shows `ExpectedInstallationDateUtc` directly).
    public OrderInstallationSummaryDto? Installation { get; set; }
}

using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.NetworkAccounts;
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

// Small linked-service summary attached to OrderDto detail responses
// so admin/customer order pages can render a "View Service" link
// straight from the order, without firing a second round-trip to
// /api/network-accounts. Surfaced only on detail/create endpoints;
// the list projection skips it to avoid an N+1 join.
public class OrderServiceSummaryDto
{
    public Guid Id { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public NetworkAccountStatus Status { get; set; }

    /// <summary>
    /// Customer-friendly status label: "Pending Installation",
    /// "Pending Payment", "Pending Activation", "Active", "Suspended",
    /// "Terminated", "Activation Failed". Derived from the order's
    /// lifecycle state + the NetworkAccount status so the portal can
    /// render a consistent tile without re-deriving the rule.
    /// </summary>
    public string DisplayStatus { get; set; } = string.Empty;
}

public class OrderDto
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public Guid? CustomerProfileId { get; set; }
    public Guid? ServicePackageId { get; set; }
    // Selected variant snapshot. PackagePrice/Fee already hold the
    // effective (variant) values; these two are for display ("4 IP").
    public Guid? ServicePackageVariantId { get; set; }
    public string? PackageVariantName { get; set; }
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
    public string? MapProviderReference { get; set; }

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

    // Customer-selectable billing day (1..31). Exposed so admin +
    // client-service detail pages can render it read-only. The durable
    // "active" anchor lives on ServiceBillingSchedule.AnchorDayOfMonth
    // once the schedule is created; this order-snapshot is the value
    // captured at checkout.
    public int PreferredBillingDay { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    // Populated on detail/create AND list responses. List rows enrich per
    // item in OrderService.ToPagedResultAsync so the customer's My Orders
    // pill stays consistent with the detail page (mock-checkout keeps the
    // order Status at Submitted, so a list mapper can't derive paid status
    // from `Status` alone — it needs this summary).
    // See `OrderPaymentSummaryDto` notes above for resolution rules.
    public OrderPaymentSummaryDto? Payment { get; set; }

    // Populated on detail/create/admin-update responses. Null on list
    // rows (the list shows `ExpectedInstallationDateUtc` directly).
    public OrderInstallationSummaryDto? Installation { get; set; }

    // Populated on detail/create/admin-update responses ONLY when a
    // NetworkAccount has been provisioned for the order. The "View
    // Service" button in the admin Order Detail page is enabled when
    // this is non-null and disabled otherwise.
    public OrderServiceSummaryDto? Service { get; set; }

    // Populated on detail responses (and list rows, same pattern as
    // Payment) ONLY when an OpenserveOrder exists for this order — null
    // for Security/Voice/LTE/Wireless orders and for Fibre orders that
    // haven't reached the submission trigger point yet. This is the
    // seam mobile/ClientZone read automatically (brief Priority 7) —
    // neither ever calls Openserve directly. Customer-safe: friendly
    // status + raw state only, no location/system identifiers.
    public OrderOpenserveSummaryDto? Openserve { get; set; }

    // Admin-only technical detail (brief §3/§6: "make the reason
    // obvious to admin"). Deliberately NEVER populated on any customer-
    // facing response (CreateMineAsync, GetMineByIdAsync, CancelMineAsync,
    // RequestAddressChangeMineAsync) — only the Admin* methods and the
    // admin branch of GetByIdInternalAsync set this. Present even before
    // an OpenserveOrder row exists (qualification runs at order-creation
    // time, before payment/submission), unlike OrderOpenserveSummaryDto.
    public OrderOpenserveAdminDto? OpenserveAdmin { get; set; }
}

public class OrderOpenserveAdminDto
{
    public string? AmId { get; set; }
    public string? BuildingNumId { get; set; }
    public DateTime? QualifiedAtUtc { get; set; }
    public string? QualificationFailureReason { get; set; }
}

// Deliberately friendly + raw side by side: FriendlyStatus is safe to
// show a customer verbatim, RawState is admin/support-only (brief §8:
// "Maintain both... Do NOT simply overwrite one status string").
public class OrderOpenserveSummaryDto
{
    public Guid OpenserveOrderId { get; set; }
    public string? OpenserveOrderNumber { get; set; }
    public string RawState { get; set; } = string.Empty;
    public string NormalizedStatus { get; set; } = string.Empty;
    public string FriendlyStatus { get; set; } = string.Empty;
    public bool IsTerminal { get; set; }
    public string? LatestDescription { get; set; }
    public DateTime? LastOpenserveUpdateAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
}

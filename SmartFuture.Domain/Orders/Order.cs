using SmartFuture.Domain.Common;
using SmartFuture.Domain.CoverageRequests;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Domain.Orders;

public class Order : BaseEntity
{
    public string OrderNumber { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid? CustomerProfileId { get; set; }
    public CustomerProfile? CustomerProfile { get; set; }

    public Guid? ServicePackageId { get; set; }
    public ServicePackage? ServicePackage { get; set; }

    // Selected package variant (e.g. "4 IP") when the package offers
    // variants. Null for variant-less packages. The Package* snapshot
    // fields below already hold the EFFECTIVE price/fee/free (variant
    // override when a variant is selected, else the package's own values),
    // so downstream billing needs no variant awareness. VariantName is a
    // display snapshot for the order/invoice views.
    public Guid? ServicePackageVariantId { get; set; }
    public ServicePackageVariant? ServicePackageVariant { get; set; }
    public string? PackageVariantName { get; set; }

    public Guid? CoverageRequestId { get; set; }
    public CoverageRequest? CoverageRequest { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Draft;
    public OrderSource Source { get; set; } = OrderSource.CustomerApp;

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

    /// <summary>
    /// Customer-declared residence/property type. Null on every order
    /// created before this field existed — historical orders read and
    /// display fine with no value here, and nothing infers/backfills
    /// one. Required (at the service layer, not the DB) for new Fibre
    /// orders only; optional for every other package type.
    /// </summary>
    public PropertyType? PropertyType { get; set; }

    /// <summary>Building / complex / estate / business park name — only meaningful when PropertyType is Apartment/Townhouse/ComplexEstate/StudentResidence/BusinessOffice. Deliberately separate from AddressLine2 (free text) so admin/install tooling can rely on a structured value.</summary>
    public string? BuildingComplexName { get; set; }

    /// <summary>Unit / flat / suite number. Required at the service layer when PropertyType is Apartment; optional/blank for a freestanding House.</summary>
    public string? UnitNumber { get; set; }

    /// <summary>
    /// Openserve Address Master Identifier (level 6 AMID) — mandatory
    /// on every Openserve productOrder "place" (Fulfilment API Spec
    /// Appendix C: place.amid is 1:N / not nullable). Nothing in this
    /// codebase currently populates this today; the public coverage
    /// check (OpenserveFibreCoverageProvider) hits a different,
    /// unauthenticated GIS endpoint that does not return AMID. The
    /// authoritative source is Openserve's authenticated Product
    /// Qualification API (spec §3), which is out of scope for the
    /// current Openserve fulfilment phase — until that's wired up,
    /// orders without a value here are blocked from Openserve
    /// submission (see OpenserveOrderSubmissionService), not silently
    /// sent with a fabricated AMID.
    /// </summary>
    public string? OpenserveAmId { get; set; }

    /// <summary>Openserve "buildingNumId" (BLD_NUM_ID from the Product Qualification API's buildingInfo) — only populated for an MDU address where exactly one building/unit was returned, or where the customer's UnitNumber matched exactly one row (see OpenserveBuildingMatcher). A null value never blocks submission.</summary>
    public string? OpenserveBuildingNumId { get; set; }

    // The other three MDU place values Postman UC 1 requires "exactly per
    // product qualification API" — captured from the SAME buildingInfo row
    // as OpenserveBuildingNumId and sent verbatim on Create Order. Kept
    // separate from the customer-typed BuildingComplexName/UnitNumber
    // above, which are free text and never sent to Openserve as-is.

    /// <summary>BUILDING_NAME from Product Qualification, verbatim.</summary>
    public string? OpenserveBuildingName { get; set; }

    /// <summary>FLOOR from Product Qualification, verbatim.</summary>
    public string? OpenserveFloor { get; set; }

    /// <summary>NUM (unit number) from Product Qualification, verbatim.</summary>
    public string? OpenserveUnit { get; set; }

    /// <summary>How many buildingInfo rows Openserve's Product Qualification returned for this address. More than one with no OpenserveBuildingNumId = the unit still needs resolving, and submission is blocked. Null = not recorded (qualified before candidates were stored).</summary>
    public int? OpenserveBuildingCandidateCount { get; set; }

    /// <summary>The buildingInfo rows Openserve returned, verbatim, as JSON — what Admin picks from when the customer's unit can't be matched deterministically. Only ever written from a qualification response.</summary>
    public string? OpenserveBuildingCandidatesJson { get; set; }

    /// <summary>When the Product Qualification API lookup last ran for this order's address (success or failure) — null if it never ran (integration disabled, or order predates this field).</summary>
    public DateTime? OpenserveQualifiedAtUtc { get; set; }

    /// <summary>Human-readable reason OpenserveAmId is still null after a qualification attempt (e.g. "no coverage at this address", "Openserve qualification disabled", transport failure) — surfaced to admin so a missing AMID is never a silent mystery.</summary>
    public string? OpenserveQualificationFailureReason { get; set; }

    // Admin "Pause Openserve automation" for an exceptional order. While
    // set, nothing sends this order to Openserve — not the automatic
    // trigger, the recovery worker, the safety sweep, or a manual send.
    // Reconciliation of an order Openserve already has is unaffected.
    // Lives on the Order (not OpenserveOrder) so it can be set before any
    // submission record exists. Who/when/why is also in the audit log.
    public bool OpenserveAutomationPaused { get; set; }
    public DateTime? OpenserveAutomationPausedAtUtc { get; set; }
    public Guid? OpenserveAutomationPausedByUserId { get; set; }
    public string? OpenserveAutomationPauseReason { get; set; }

    public string? CustomerNotes { get; set; }
    public string? AdminNotes { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }

    // ─── Recurring-billing anchor (go-live alignment) ───────────────
    //
    // BillingAnchorDateUtc is the immutable anchor day — usually the
    // admin's "Activate Service" date — that all future monthly
    // invoices anchor to.
    //
    // NextPayDateUtc is the next invoice-due date for this order.
    // Critical rule: when a customer pays early, NextPayDateUtc must
    // advance from the previous due date (i.e. NextPayDateUtc itself),
    // not from the payment date. That keeps billing on a true monthly
    // cadence regardless of payment timing.
    public DateTime? BillingAnchorDateUtc { get; set; }
    public DateTime? NextPayDateUtc { get; set; }

    // ─── Admin Openserve activation audit (go-live alignment) ───────
    //
    // Set by the admin "Mark service activated on Openserve" action.
    // OpenserveActivationReference is a free-text field for the
    // Openserve ticket / activation reference so admin can correlate
    // SmartFuture orders with Openserve operations.
    public string? OpenserveActivationReference { get; set; }
    public string? ActivationNotes { get; set; }
    public Guid? ActivatedByUserId { get; set; }
    public User? ActivatedByUser { get; set; }

    // Phase 44 — separate "what the customer asked for" from "what
    // admin actually scheduled". RequestedInstallationDateUtc is set
    // exactly once during customer order creation and is read-only
    // afterwards. ExpectedInstallationDateUtc continues to be the
    // admin-confirmed/scheduled date and changes as the installation
    // is rescheduled.
    public DateTime? RequestedInstallationDateUtc { get; set; }
    public DateTime? ExpectedInstallationDateUtc { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public User? LastStatusChangedByUser { get; set; }

    public string? CancellationReason { get; set; }
    public string? FailureReason { get; set; }
    public string? RejectionReason { get; set; }

    // Day of month the customer picked at checkout for their monthly
    // billing cycle (e.g. 15, 25, 30). Must match one of the currently-
    // enabled BillingDayOption rows at write time; existing legacy
    // orders were backfilled to 30 by the migration. This is a checkout
    // snapshot — the durable "active" value lives on
    // ServiceBillingSchedule.AnchorDayOfMonth once the schedule is
    // anchored.
    public int PreferredBillingDay { get; set; } = 30;

    // One-shot idempotency stamp for the "first pro-rata invoice"
    // generated by AdminActivateServiceAsync (Fibre) or by the intent
    // conversion (Security). Prevents a re-run of admin activation from
    // producing a second pro-rata line and prevents the intent handler
    // from double-charging Security customers on retry.
    public DateTime? FirstProRataInvoiceGeneratedAtUtc { get; set; }
}

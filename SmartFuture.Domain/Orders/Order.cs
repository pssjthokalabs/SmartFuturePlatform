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
}

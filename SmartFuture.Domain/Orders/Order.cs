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

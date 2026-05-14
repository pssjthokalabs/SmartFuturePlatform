using SmartFuture.Domain.Common;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.CoverageRequests;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Domain.CoverageRequests;

public class CoverageRequest : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid? CustomerProfileId { get; set; }
    public CustomerProfile? CustomerProfile { get; set; }

    public Guid? ServicePackageId { get; set; }
    public ServicePackage? ServicePackage { get; set; }

    public ServicePackageType? RequestedServiceType { get; set; }

    public CoverageRequestStatus Status { get; set; } = CoverageRequestStatus.Submitted;
    public CoverageRequestSource Source { get; set; } = CoverageRequestSource.CustomerApp;

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
    public string? CoverageResultSummary { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }
}

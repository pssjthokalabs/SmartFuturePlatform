using SmartFuture.Shared.Enums.CoverageRequests;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.CoverageRequests.Dtos;

public class CoverageRequestDto
{
    public Guid Id { get; set; }
    // Nullable: anonymous public submissions from the marketing site
    // have no signed-in user. Authenticated requests still carry an id.
    public Guid? UserId { get; set; }
    public Guid? CustomerProfileId { get; set; }
    public Guid? ServicePackageId { get; set; }
    public string? ServicePackageName { get; set; }

    public ServicePackageType? RequestedServiceType { get; set; }
    public CoverageRequestStatus Status { get; set; }
    public CoverageRequestSource Source { get; set; }

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
    public string? CoverageResultSummary { get; set; }

    /// <summary>
    /// Semicolon-separated catalogue of services the visitor asked
    /// about (e.g. "Fibre;Wireless Internet;Voice Solutions"). Portal
    /// splits on ';' and renders one coloured pill per service. Null
    /// on rows created before this field shipped — portal defaults
    /// those to a single "Fibre" pill.
    /// </summary>
    public string? Services { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewedByUserEmail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

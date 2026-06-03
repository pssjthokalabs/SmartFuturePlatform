using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.CoverageRequests.Dtos;

public class AdminUpdateCoverageRequestDto
{
    public Guid? ServicePackageId { get; set; }
    public ServicePackageType? RequestedServiceType { get; set; }

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

    public string? AdminNotes { get; set; }
    public string? CoverageResultSummary { get; set; }

    /// <summary>
    /// Optional admin edit of the services list. Same accepted values
    /// as the public Create DTO. When null, the existing services on
    /// the entity stay unchanged (admin update is a partial edit by
    /// convention — empty list != null list).
    /// </summary>
    public List<string>? Services { get; set; }
}

using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.CoverageRequests.Dtos;

public class CreateCoverageRequestDto
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

    public string? CustomerNotes { get; set; }

    /// <summary>
    /// Services the visitor wants to be contacted about, in any order.
    /// Accepted values: "Fibre", "Wireless Internet", "Voice Solutions"
    /// (case-insensitive; trimmed; deduplicated). When omitted, the
    /// service normalises to ["Fibre"] for backwards compatibility with
    /// older website builds that didn't send this field. Unknown values
    /// are dropped silently so a stale frontend can't break the API.
    /// </summary>
    public List<string>? Services { get; set; }
}

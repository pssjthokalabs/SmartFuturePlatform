namespace SmartFuture.Application.Installations.Dtos;

public class AdminUpdateInstallationRequestDto
{
    public DateTime? ScheduledForUtc { get; set; }

    public string? TechnicianName { get; set; }
    public string? TechnicianPhone { get; set; }
    public string? TechnicianEmail { get; set; }
    public Guid? TechnicianUserId { get; set; }

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
    public string? TechnicianNotes { get; set; }
}

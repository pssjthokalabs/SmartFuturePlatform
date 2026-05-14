namespace SmartFuture.Application.CustomerProfiles.Dtos;

public class CustomerProfileDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public string? IdNumber { get; set; }

    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    public string? Notes { get; set; }

    public string? PreferredContactMethod { get; set; }
    public bool AcceptsMarketing { get; set; }
    public bool AcceptsPaymentReminders { get; set; }
    public bool AcceptsInstallationUpdates { get; set; }
    public bool AcceptsNetworkAlerts { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

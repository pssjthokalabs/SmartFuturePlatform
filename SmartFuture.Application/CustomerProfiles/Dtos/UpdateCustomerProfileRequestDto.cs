namespace SmartFuture.Application.CustomerProfiles.Dtos;

public class UpdateCustomerProfileRequestDto
{
    // Customer-editable identity fields. Email and PhoneNumber are
    // deliberately omitted from this DTO — changing those requires a
    // verification flow (OTP / email confirmation + uniqueness check)
    // that the current backend does not implement. The portal hides
    // those inputs behind a "contact support" helper instead.
    public string? FirstName { get; set; }
    public string? LastName { get; set; }

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
    public bool AcceptsPaymentReminders { get; set; } = true;
    public bool AcceptsInstallationUpdates { get; set; } = true;
    public bool AcceptsNetworkAlerts { get; set; } = true;
}

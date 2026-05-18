namespace SmartFuture.Application.CustomerProfiles.Dtos;

public class CustomerProfileDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    // Identity fields denormalized from the User entity so the
    // customer portal can read the full profile in a single call.
    // Email and PhoneNumber are output-only here — they are not
    // accepted by the update endpoint until a verification flow
    // exists for those changes.
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }

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

namespace SmartFuture.Application.Customers.Admin.Dtos;

public class AdminCustomerDetailDto
{
    public Guid Id { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    public string AccountStatus { get; set; } = "Active";

    public string? IdNumber { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }

    public string? PreferredContactMethod { get; set; }
    public bool AcceptsMarketing { get; set; }
    public bool AcceptsPaymentReminders { get; set; }
    public bool AcceptsInstallationUpdates { get; set; }
    public bool AcceptsNetworkAlerts { get; set; }

    public string? Notes { get; set; }

    public string? ActivePackageName { get; set; }
    public string? ServiceType { get; set; }

    public decimal Outstanding { get; set; }

    public decimal? LastPaymentAmount { get; set; }
    public DateTime? LastPaymentAtUtc { get; set; }

    public int OpenTickets { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

namespace SmartFuture.Application.Customers.Admin.Dtos;

// Identity-bearing fields (Email, PhoneNumber) are NOT updated here.
// Those flow through ASP.NET Identity's confirmation pipelines and need a
// dedicated endpoint with confirmation/notification side effects. The
// frontend may send them in the body, but they're silently ignored on
// the server until that endpoint exists.
public class UpdateAdminCustomerRequestDto
{
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

    public string? PreferredContactMethod { get; set; }
    public bool? AcceptsMarketing { get; set; }
    public bool? AcceptsPaymentReminders { get; set; }
    public bool? AcceptsInstallationUpdates { get; set; }
    public bool? AcceptsNetworkAlerts { get; set; }

    public string? Notes { get; set; }
}

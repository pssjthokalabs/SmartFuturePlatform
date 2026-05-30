using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;

namespace SmartFuture.Domain.Customers;

public class CustomerProfile : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

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

    // Phase 3 — customer-controlled opt-out for the auto-debit job
    // (Phase 5/6). False by default until the customer explicitly
    // opts in on the Settings page or by paying the installation fee
    // with a saved-card consent box ticked. The job MUST short-circuit
    // when this is false even if an active reusable mandate exists.
    public bool AutoBillingEnabled { get; set; }
}

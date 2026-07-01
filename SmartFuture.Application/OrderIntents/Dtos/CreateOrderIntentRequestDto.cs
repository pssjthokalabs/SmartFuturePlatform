namespace SmartFuture.Application.OrderIntents.Dtos;

// Body for POST /api/public/order-intents. Field names mirror the
// public-website wizard. Validation lives in OrderIntentService —
// anything that would block a real order (missing address line, bad
// email shape, etc.) is rejected here too so visitors get the error
// before they leave the wizard.
public class CreateOrderIntentRequestDto
{
    public Guid ServicePackageId { get; set; }

    // Optional selected variant chosen on the public website. Preserved
    // on the intent so a ClientZone continuation keeps the customer's
    // choice. Null → no variant.
    public Guid? ServicePackageVariantId { get; set; }

    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    public string? AddressLine1 { get; set; }
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

    public DateTime? RequestedInstallationDateUtc { get; set; }
    public string? CustomerNotes { get; set; }

    // Free-form provenance ("Website", "Mobile") — capped at 50 chars
    // server-side. Optional; useful for funnel analytics later.
    public string? Source { get; set; }
}

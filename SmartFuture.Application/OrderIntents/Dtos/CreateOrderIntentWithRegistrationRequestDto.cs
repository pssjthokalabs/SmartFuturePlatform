namespace SmartFuture.Application.OrderIntents.Dtos;

// Body for POST /api/public/order-intents/register. Strict superset of
// CreateOrderIntentRequestDto: same wizard-collected order fields plus
// the password the visitor chose for their new Client Zone account.
//
// Server-side validation is the source of truth — the website does
// best-effort checks first, but the real password rules (Identity) and
// duplicate detection run here.
public class CreateOrderIntentWithRegistrationRequestDto
{
    public Guid ServicePackageId { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;

    // Phase 9 — legal consent. Both flags must be true and a version
    // string must be supplied before the backend will create the user
    // and intent. Version strings are short YYYY-MM tags shared with
    // the website's legalVersions.js.
    public bool AcceptedTerms { get; set; }
    public string? TermsVersion { get; set; }

    public bool PrivacyAcknowledged { get; set; }
    public string? PrivacyVersion { get; set; }

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

    public DateTime? RequestedInstallationDateUtc { get; set; }
    public string? CustomerNotes { get; set; }

    public string? Source { get; set; }
}

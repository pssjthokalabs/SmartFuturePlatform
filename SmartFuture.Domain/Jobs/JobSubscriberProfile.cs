using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;

namespace SmartFuture.Domain.Jobs;

// Job-specific extras for a user who holds the JobSubscriber role.
// Deliberately does NOT duplicate FirstName / LastName / Email /
// Cellphone — those live on the Users row and stay the single source of
// truth. One profile per user (unique index on UserId).
//
// Greenhouse-inspired minus the US-only questions: no work
// authorisation, no visa sponsorship.
public class JobSubscriberProfile : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public string? PreferredFirstName { get; set; }

    // Only set when the subscriber wants job correspondence on a
    // different number to their account phone. Null = use User.PhoneNumber.
    public string? ContactPhone { get; set; }
    public string? ContactPhoneNormalized { get; set; }

    public string? CurrentCity { get; set; }
    public string? CurrentProvince { get; set; }
    public string? CurrentCountry { get; set; } = "South Africa";

    public string? LinkedInProfileUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? SalaryExpectations { get; set; }
    public string? HighestQualification { get; set; }
    public int? YearsOfExperience { get; set; }

    // JSON string arrays — the application layer maps to List<string>.
    public string? PreferredCategoriesJson { get; set; }
    public string? PreferredLocationsJson { get; set; }

    // CV is required to complete the profile. The URL is a
    // convenience/audit field only; downloads ALWAYS go through the
    // authenticated endpoint, which resolves the object key. Private R2
    // objects must never be linked to directly.
    public string? CvObjectKey { get; set; }
    public string? CvFileUrl { get; set; }
    public string? CvFileName { get; set; }
    public string? CvContentType { get; set; }
    public long? CvSizeBytes { get; set; }
    public DateTime? CvUploadedAtUtc { get; set; }

    public string? CoverLetterObjectKey { get; set; }
    public string? CoverLetterFileUrl { get; set; }
    public string? CoverLetterFileName { get; set; }
    public string? CoverLetterContentType { get; set; }
    public long? CoverLetterSizeBytes { get; set; }
    public DateTime? CoverLetterUploadedAtUtc { get; set; }

    // Set the first time every required field (incl. the CV) is present.
    // Null means "enrolled but profile incomplete" — the mobile/website
    // UI uses this to route the user back into the wizard.
    public DateTime? CompletedAtUtc { get; set; }
}

using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Dtos;

// What GET /api/job-subscribers/me returns. Identity fields are read
// from the Users row (single source of truth); everything else comes
// from JobSubscriberProfile.
public class JobSubscriberMeDto
{
    public Guid UserId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    public bool IsJobSubscriber { get; set; }
    public bool IsCustomer { get; set; }
    public bool HasProfile { get; set; }
    public bool IsProfileComplete { get; set; }
    // Field names the caller still has to supply before the profile can
    // be marked complete. Drives the mobile/website wizard.
    public IReadOnlyList<string> MissingFields { get; set; } = Array.Empty<string>();

    public JobSubscriberProfileDto? Profile { get; set; }
    public JobAlertPreferenceDto? AlertPreference { get; set; }
}

public class JobSubscriberProfileDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public string? PreferredFirstName { get; set; }
    public string? ContactPhone { get; set; }

    public string? CurrentCity { get; set; }
    public string? CurrentProvince { get; set; }
    public string? CurrentCountry { get; set; }

    public string? LinkedInProfileUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? SalaryExpectations { get; set; }
    public string? HighestQualification { get; set; }
    public int? YearsOfExperience { get; set; }

    public IReadOnlyList<string> PreferredCategories { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> PreferredLocations { get; set; } = Array.Empty<string>();

    // Document METADATA only. The object key and any storage URL are
    // deliberately NOT on this DTO — CVs are private, and downloads go
    // through the authenticated endpoints.
    public bool HasCv { get; set; }
    public string? CvFileName { get; set; }
    public long? CvSizeBytes { get; set; }
    public DateTime? CvUploadedAtUtc { get; set; }

    public bool HasCoverLetter { get; set; }
    public string? CoverLetterFileName { get; set; }
    public long? CoverLetterSizeBytes { get; set; }
    public DateTime? CoverLetterUploadedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

// Self-service enrolment. Two shapes in one endpoint:
//   • Signed-in caller (existing Customer or anyone) → Password/email
//     ignored; the JobSubscriber role is added to the caller.
//   • Anonymous caller → Email + Password required. If the email
//     already belongs to a user we do NOT create a duplicate and we do
//     NOT reveal that it exists beyond asking them to sign in.
public class RegisterJobSubscriberRequestDto
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }

    // Optional profile fields captured on the same form.
    public string? CurrentCity { get; set; }
    public string? CurrentProvince { get; set; }
    public List<string>? PreferredCategories { get; set; }
    public List<string>? PreferredLocations { get; set; }
    public bool SubscribeToAlerts { get; set; } = true;
}

public class UpsertJobSubscriberProfileRequestDto
{
    public string? PreferredFirstName { get; set; }
    public string? ContactPhone { get; set; }
    public string? CurrentCity { get; set; }
    public string? CurrentProvince { get; set; }
    public string? CurrentCountry { get; set; }
    public string? LinkedInProfileUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? SalaryExpectations { get; set; }
    public string? HighestQualification { get; set; }
    public int? YearsOfExperience { get; set; }
    public List<string>? PreferredCategories { get; set; }
    public List<string>? PreferredLocations { get; set; }
}

// Returned by the upload endpoints — metadata only, never a URL that
// would let the file be fetched without authentication.
public class JobSubscriberDocumentDto
{
    public JobSubscriberDocumentType DocumentType { get; set; }
    public string DocumentTypeLabel { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime UploadedAtUtc { get; set; }
}

// ─── Admin surface ────────────────────────────────────────────────────

public class AdminJobSubscriberListItemDto
{
    public Guid UserId { get; set; }
    public int? UserNumber { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string AccountStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    // Role pills — the same user can legitimately be both.
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
    public bool IsCustomer { get; set; }
    public bool IsJobSubscriber { get; set; }

    public string? CurrentCity { get; set; }
    public string? CurrentProvince { get; set; }
    public bool HasCv { get; set; }
    public bool HasCoverLetter { get; set; }
    public bool IsProfileComplete { get; set; }
    public bool AlertsSubscribed { get; set; }
    public DateTime? ProfileCompletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class AdminJobSubscriberDetailDto
{
    public AdminJobSubscriberListItemDto Summary { get; set; } = new();
    public JobSubscriberProfileDto? Profile { get; set; }
    public JobAlertPreferenceDto? AlertPreference { get; set; }
    public IReadOnlyList<JobSubscriberDocumentDto> Documents { get; set; } = Array.Empty<JobSubscriberDocumentDto>();
}

public class AdminJobSubscriberFilterRequestDto
{
    public string? Search { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public bool? HasCv { get; set; }
    public bool? IsProfileComplete { get; set; }
    public bool? AlertsSubscribed { get; set; }
    // Narrow to users who are BOTH Customer and JobSubscriber.
    public bool? IsAlsoCustomer { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

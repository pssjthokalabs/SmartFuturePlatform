using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Dtos;

// Admin list/detail projection. Superset of the public DTO: carries
// provenance, moderation state, and the import bookkeeping fields.
public class AdminJobOpportunityDto
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? CompanyName { get; set; }

    public string? Location { get; set; }
    public string? Country { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }

    public string? Category { get; set; }
    public JobWorkplaceType WorkplaceType { get; set; }
    public string WorkplaceTypeLabel { get; set; } = string.Empty;
    public string? EmploymentType { get; set; }
    public string? SalaryText { get; set; }

    public string? Summary { get; set; }
    public string? DescriptionHtml { get; set; }
    public string? DescriptionText { get; set; }
    public string? RequirementsText { get; set; }
    public string? ApplicationInstructions { get; set; }
    public string? ApplyUrl { get; set; }
    public string? ApplyEmail { get; set; }

    public string? SourceUrl { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    public string? ExternalId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;

    public DateTime? PostedDateUtc { get; set; }
    public DateTime? ClosingDateUtc { get; set; }
    public DateTime ImportedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }

    public JobOpportunityStatus Status { get; set; }
    public string StatusLabel { get; set; } = string.Empty;
    public bool IsFeatured { get; set; }
    public bool IsManuallyEdited { get; set; }
    public bool IsExpired { get; set; }

    public string? LogoUrl { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public int ViewCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class AdminJobOpportunityFilterRequestDto
{
    public string? Search { get; set; }
    public Guid? SourceId { get; set; }
    public string? SourceName { get; set; }
    public JobOpportunityStatus? Status { get; set; }
    public string? Category { get; set; }
    public string? Location { get; set; }
    public JobWorkplaceType? WorkplaceType { get; set; }
    public bool? IsFeatured { get; set; }
    // True → only rows whose closing date has passed. False → only rows
    // that are still open (or have no closing date). Null → no filter.
    public bool? IsExpired { get; set; }

    public int? Page { get; set; }
    public int? PageSize { get; set; }
    public string? Sort { get; set; }
}

// Admin manual capture. Title is the only hard requirement — a job
// posted by phone/WhatsApp often has nothing else at first.
public class CreateJobOpportunityRequestDto
{
    public string Title { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? Location { get; set; }
    public string? Country { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? Category { get; set; }
    public JobWorkplaceType? WorkplaceType { get; set; }
    public string? EmploymentType { get; set; }
    public string? SalaryText { get; set; }
    public string? Summary { get; set; }
    public string? DescriptionHtml { get; set; }
    public string? DescriptionText { get; set; }
    public string? RequirementsText { get; set; }
    public string? ApplicationInstructions { get; set; }
    public string? ApplyUrl { get; set; }
    public string? ApplyEmail { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceName { get; set; }
    public Guid? SourceId { get; set; }
    public DateTime? PostedDateUtc { get; set; }
    public DateTime? ClosingDateUtc { get; set; }
    public bool? IsFeatured { get; set; }
    public string? LogoUrl { get; set; }
    public List<string>? Tags { get; set; }
    // Draft unless the admin explicitly publishes on create.
    public bool PublishImmediately { get; set; }
}

// Every field is optional — null means "leave unchanged", so the admin
// form can PATCH-style submit only what it edited.
public class UpdateJobOpportunityRequestDto
{
    public string? Title { get; set; }
    public string? CompanyName { get; set; }
    public string? Location { get; set; }
    public string? Country { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? Category { get; set; }
    public JobWorkplaceType? WorkplaceType { get; set; }
    public string? EmploymentType { get; set; }
    public string? SalaryText { get; set; }
    public string? Summary { get; set; }
    public string? DescriptionHtml { get; set; }
    public string? DescriptionText { get; set; }
    public string? RequirementsText { get; set; }
    public string? ApplicationInstructions { get; set; }
    public string? ApplyUrl { get; set; }
    public string? ApplyEmail { get; set; }
    public DateTime? PostedDateUtc { get; set; }
    public DateTime? ClosingDateUtc { get; set; }
    public bool? IsFeatured { get; set; }
    public string? LogoUrl { get; set; }
    public List<string>? Tags { get; set; }
}

using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Dtos;

// One wire shape for both the public list and the public detail. The
// list projection leaves the long-form fields null; the detail
// projection fills them in — unless the module is configured
// subscribers-only and the caller isn't one, in which case
// `RequiresSubscription` is true and the gated fields stay null.
public class JobOpportunityDto
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
    public string? LogoUrl { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    public DateTime? PostedDateUtc { get; set; }
    public DateTime? ClosingDateUtc { get; set; }
    public bool IsClosingSoon { get; set; }
    public bool IsExpired { get; set; }
    public bool IsFeatured { get; set; }

    public string SourceName { get; set; } = string.Empty;
    // Always visible — the reader must be able to verify the listing at
    // its origin, subscriber-gated or not.
    public string? SourceUrl { get; set; }

    // ─── Detail-only, subscriber-gated when JobDetailsSubscribersOnly ───
    public string? DescriptionHtml { get; set; }
    public string? DescriptionText { get; set; }
    public string? RequirementsText { get; set; }
    public string? ApplicationInstructions { get; set; }
    public string? ApplyUrl { get; set; }
    public string? ApplyEmail { get; set; }

    // True when the gated fields were withheld. The website/app renders
    // a teaser plus a "subscribe to see full details" call to action
    // rather than a bare 403.
    public bool RequiresSubscription { get; set; }
}

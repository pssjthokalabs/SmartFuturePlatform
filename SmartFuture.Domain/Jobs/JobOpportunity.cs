using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Domain.Jobs;

// A single job listing — imported from a JobSource or captured by an
// admin (SourceId null, SourceName "Manual").
//
// De-duplication contract: `Fingerprint` is a SHA-256 hex digest of
// SourceUrl + Title + CompanyName + Location, normalised. It carries a
// UNIQUE index, so a re-crawl of the same listing UPDATEs the existing
// row (refreshing LastSeenAtUtc) instead of inserting a twin. See
// JobFingerprint in the Application layer for the exact recipe.
public class JobOpportunity : BaseEntity
{
    public string Title { get; set; } = string.Empty;
    // URL-safe identifier used by the public detail route
    // (/api/jobs/{slugOrId}). Unique; collisions get a numeric suffix.
    public string Slug { get; set; } = string.Empty;

    public string? CompanyName { get; set; }

    // Free-text location exactly as the source stated it, plus the
    // parsed components. The public location filter matches against
    // `Location`, and the facet list is built from City/Province.
    public string? Location { get; set; }
    public string? Country { get; set; } = "South Africa";
    public string? Province { get; set; }
    public string? City { get; set; }

    public string? Category { get; set; }
    public JobWorkplaceType WorkplaceType { get; set; } = JobWorkplaceType.Unknown;
    public string? EmploymentType { get; set; }
    public string? SalaryText { get; set; }

    // Short card blurb. Always populated — falls back to the first
    // ~300 characters of the description when the source has no summary.
    public string? Summary { get; set; }
    // Sanitised HTML (script/style/iframe/event handlers stripped) when
    // the source gave us markup we trust; otherwise null.
    public string? DescriptionHtml { get; set; }
    // Plain-text description. ALWAYS populated — this is what the
    // mobile app renders and what search matches against.
    public string? DescriptionText { get; set; }
    public string? RequirementsText { get; set; }

    // How to apply, in words. For sources that only publish an email
    // address this holds "Send your CV to jobs@example.co.za" and
    // ApplyEmail carries the address.
    public string? ApplicationInstructions { get; set; }
    public string? ApplyUrl { get; set; }
    public string? ApplyEmail { get; set; }

    // Provenance — always kept visible on the public detail page so a
    // reader can verify the listing at its origin.
    public string? SourceUrl { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    public JobSource? Source { get; set; }
    // The source's own id for the listing (RSS guid, JSON-LD identifier).
    // Advisory only — Fingerprint is the authority on duplicates.
    public string? ExternalId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;

    public DateTime? PostedDateUtc { get; set; }
    public DateTime? ClosingDateUtc { get; set; }
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    // Bumped every time a crawl still sees the listing. Lets the admin
    // spot jobs that vanished from their source without a closing date.
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;

    public JobOpportunityStatus Status { get; set; } = JobOpportunityStatus.Draft;
    public bool IsFeatured { get; set; }
    // True once an admin has edited the row by hand. The importer then
    // refreshes ONLY LastSeenAtUtc on re-crawl so manual corrections are
    // never clobbered by messy source content.
    public bool IsManuallyEdited { get; set; }

    public string? LogoUrl { get; set; }
    // JSON string array of tags/skills; the application layer
    // serialises/deserialises to List<string>.
    public string? TagsJson { get; set; }

    // Trimmed copy of what we fetched, for debugging a bad extraction.
    public string? RawContentSnapshot { get; set; }

    public int ViewCount { get; set; }
}

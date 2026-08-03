using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Import;

// One job as understood by an extractor, before it is reconciled with
// the database. Everything is nullable because real-world sources are
// incomplete; the importer decides what is good enough to store.
public class ExtractedJob
{
    public string? Title { get; set; }
    public string? CompanyName { get; set; }
    public string? Location { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Category { get; set; }
    public JobWorkplaceType WorkplaceType { get; set; } = JobWorkplaceType.Unknown;
    public string? EmploymentType { get; set; }
    public string? SalaryText { get; set; }

    public string? Summary { get; set; }
    public string? DescriptionHtml { get; set; }
    public string? DescriptionText { get; set; }
    public string? RequirementsText { get; set; }

    public string? ApplicationInstructions { get; set; }
    public string? ApplyUrl { get; set; }
    public string? ApplyEmail { get; set; }

    // The canonical URL of THIS listing. Falls back to the source URL
    // when a board publishes several roles on one page.
    public string? SourceUrl { get; set; }
    public string? ExternalId { get; set; }
    public string? LogoUrl { get; set; }
    public List<string> Tags { get; set; } = new();

    public DateTime? PostedDateUtc { get; set; }
    public DateTime? ClosingDateUtc { get; set; }

    public string? RawContentSnapshot { get; set; }

    // Which strategy produced this row ("json-ld", "rss", "article").
    // Recorded in the run notes so a bad extraction is traceable.
    public string ExtractionStrategy { get; set; } = "unknown";
}

// What one extraction pass produced, plus any human-readable notes for
// the run log (skipped items, why a strategy bailed out).
public class JobExtractionResult
{
    public List<ExtractedJob> Jobs { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public string StrategyUsed { get; set; } = "none";

    // Populated instead of Jobs when the page turned out to be an
    // archive. The import service fetches these; the archive itself is
    // never stored as an opportunity.
    public List<string> ChildUrls { get; set; } = new();
    public string? NextPageUrl { get; set; }

    public static JobExtractionResult Empty(string note) => new()
    {
        Notes = new List<string> { note }
    };
}

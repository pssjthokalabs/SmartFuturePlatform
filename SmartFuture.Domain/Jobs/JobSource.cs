using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Domain.Jobs;

// An admin-configured place we pull job opportunities from. Health is
// tracked on the row itself (LastCheckedAt / LastSuccessAt /
// LastFailureAt / LastFailureMessage) so the admin Sources page can
// show "this board started blocking us" without opening the run log.
public class JobSource : BaseEntity
{
    public string SourceName { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public JobSourceType SourceType { get; set; } = JobSourceType.HtmlPage;

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    // Applied to every job imported from this source when the extractor
    // can't infer a category from the content itself.
    public string? DefaultCategory { get; set; }
    // Same idea for location — many single-region boards never repeat
    // the province/city inside each listing.
    public string? DefaultLocation { get; set; }

    // Null = only crawled when an admin presses "Refresh now". A value
    // makes the source eligible for the scheduled importer.
    public int? CrawlFrequencyMinutes { get; set; }

    // When false, imported jobs land as Draft and an admin publishes
    // them. Defaults to FALSE: crawled content is third-party text of
    // unknown quality, and a bad extraction that reaches the public site
    // costs more than one that waits in a review queue. A source that
    // has proven itself can be switched to auto-publish per source.
    public bool AutoPublish { get; set; }

    // Safety valve so one enormous listings page can't flood the table
    // on a single run. Since archives are crawled post-by-post this is
    // also the outbound HTTP request budget, which is why the default is
    // deliberately modest.
    public int MaxJobsPerRun { get; set; } = 20;

    // How many pages of a paginated archive to walk. 1 = the configured
    // URL only. Kept low by default: following "next" indefinitely is
    // how a crawler ends up downloading an entire site.
    public int MaxPagesPerRun { get; set; } = 1;

    public DateTime? LastCheckedAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public DateTime? LastFailureAtUtc { get; set; }
    public string? LastFailureMessage { get; set; }

    // Rolling counters shown on the admin Sources page.
    public int ConsecutiveFailureCount { get; set; }
    public int TotalJobsImported { get; set; }

    public ICollection<JobOpportunity> Jobs { get; set; } = new List<JobOpportunity>();
}

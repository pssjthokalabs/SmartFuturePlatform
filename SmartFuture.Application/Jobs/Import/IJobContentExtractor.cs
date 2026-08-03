using SmartFuture.Domain.Jobs;

namespace SmartFuture.Application.Jobs.Import;

// Turns fetched bytes into candidate jobs. Pure and synchronous — no
// network, no database — so extraction behaviour is unit-testable
// against saved fixtures. Fetching the child pages of an archive is the
// import service's job, which is why Analyze only REPORTS the URLs.
public interface IJobContentExtractor
{
    // pageUrl is the URL this content was actually fetched from, which
    // is the child URL when crawling an archive's posts. It becomes the
    // listing's SourceUrl and the base for resolving relative links.
    JobExtractionResult Extract(JobSource source, string content, string? contentType, DateTime nowUtc, string? pageUrl = null);

    // Listing page or detail page, plus the child URLs when it's a
    // listing.
    JobPageAnalysis Analyze(JobSource source, string content, string? contentType, string? pageUrl = null);
}

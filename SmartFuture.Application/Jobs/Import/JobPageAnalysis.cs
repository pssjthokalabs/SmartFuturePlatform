namespace SmartFuture.Application.Jobs.Import;

public enum JobPageKind
{
    // A single job ad. Parse it into one JobOpportunity.
    Detail = 0,
    // An archive / category / index page. Produces CHILD URLS, never a
    // JobOpportunity of its own — importing one of these as a job is the
    // exact defect this type exists to prevent.
    Listing = 1
}

// What one page turned out to be, and what to fetch next.
public class JobPageAnalysis
{
    public JobPageKind Kind { get; set; } = JobPageKind.Detail;

    // Absolute, same-domain, de-duplicated detail URLs in page order,
    // already truncated to the caller's limit.
    public List<string> ChildUrls { get; set; } = new();

    // How many qualifying links the page actually had, BEFORE the limit
    // was applied. Without this the run log can only ever report the
    // cap back to itself — "found 5, imported 5" — which is exactly why
    // an admin couldn't tell that a page of 11 jobs had been truncated.
    public int TotalChildLinksFound { get; set; }

    public bool WasTruncatedByJobLimit => TotalChildLinksFound > ChildUrls.Count;

    // The archive's "next page" link, when the theme published one.
    public string? NextPageUrl { get; set; }

    public List<string> Notes { get; set; } = new();

    public bool IsListing => Kind == JobPageKind.Listing;
}

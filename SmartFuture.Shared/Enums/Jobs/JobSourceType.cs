namespace SmartFuture.Shared.Enums.Jobs;

// How the importer should read a configured source.
//   HtmlPage — fetch + extract (JSON-LD JobPosting first, then article text)
//   RssFeed  — parse RSS 2.0 / Atom via XDocument
//   Api      — reserved for a future JSON job feed; the importer records
//              a clear "not implemented" failure rather than guessing
//   Manual   — never crawled; the admin captures jobs by hand
public enum JobSourceType
{
    HtmlPage = 0,
    RssFeed = 1,
    Api = 2,
    Manual = 3
}

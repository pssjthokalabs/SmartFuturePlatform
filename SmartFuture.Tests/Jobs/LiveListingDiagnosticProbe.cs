using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Domain.Jobs;
using SmartFuture.Infrastructure.Jobs;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Tests.Jobs;

// Opt-in diagnostic for "the page obviously has jobs but the crawler
// found none". Dumps every stage of listing detection against a real
// URL so a theme that doesn't match our selectors can be identified
// without guessing. No database, no writes.
//
// Enable with SMARTFUTURE_LISTING_DIAG=1.
public class LiveListingDiagnosticProbe
{
    [Fact]
    public async Task Diagnose_listing_link_extraction()
    {
        if (Environment.GetEnvironmentVariable("SMARTFUTURE_LISTING_DIAG") != "1") return;

        var url = Environment.GetEnvironmentVariable("SMARTFUTURE_LISTING_DIAG_URL") ?? "https://workjob.co.za/";
        var outputPath = Environment.GetEnvironmentVariable("SMARTFUTURE_LISTING_DIAG_OUT")
            ?? Path.Combine(Path.GetTempPath(), "listing-diag.txt");

        var source = new JobSource
        {
            Id = Guid.NewGuid(),
            SourceName = "Listing diagnostic",
            SourceUrl = url,
            SourceType = JobSourceType.HtmlPage,
            IsActive = true,
            AutoPublish = false,
            MaxJobsPerRun = 5,
            MaxPagesPerRun = 1
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SmartFutureJobBot/1.0 (+https://www.smartfuture.co.za)");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-ZA,en;q=0.9");

        var fetcher = new HttpJobSourceFetcher(http, NullLogger<HttpJobSourceFetcher>.Instance);
        var report = new StringBuilder();

        report.AppendLine($"URL: {url}");
        report.AppendLine(new string('=', 78));

        var fetch = await fetcher.FetchAsync(url);
        report.AppendLine($"HTTP {fetch.StatusCode} success={fetch.IsSuccess} type={fetch.ContentType}");
        if (!fetch.IsSuccess)
        {
            report.AppendLine($"FAILED: {fetch.FailureMessage}");
            await File.WriteAllTextAsync(outputPath, report.ToString());
            return;
        }

        var raw = fetch.Content ?? string.Empty;
        var stripped = HtmlTextUtilities.StripBoilerplate(raw);

        report.AppendLine($"raw HTML length      : {raw.Length}");
        report.AppendLine($"stripped HTML length : {stripped.Length}  ({(raw.Length == 0 ? 0 : 100 - stripped.Length * 100 / raw.Length)}% removed)");
        report.AppendLine($"main content found   : {(HtmlTextUtilities.ExtractMainContent(stripped) is null ? "NO" : "yes")}");
        report.AppendLine();

        report.AppendLine("--- what StripBoilerplate deletes (largest first) ---");
        foreach (var r in HtmlTextUtilities.DescribeBoilerplateRemovals(raw)
                     .OrderByDescending(x => int.Parse(Regex.Match(x, @"len=(\d+)").Groups[1].Value))
                     .Take(12))
        {
            report.AppendLine($"  {r}");
        }
        report.AppendLine();

        report.AppendLine("--- structural counts (raw vs stripped) ---");
        foreach (var tag in new[] { "article", "main", "h1", "h2", "h3" })
        {
            var name = tag;
            var inRaw = HtmlTextUtilities.ExtractElementsInner(raw, (n, _) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase), 500).Count;
            var inStripped = HtmlTextUtilities.ExtractElementsInner(stripped, (n, _) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase), 500).Count;
            report.AppendLine($"  <{tag,-8}> raw={inRaw,-4} stripped={inStripped}");
        }

        report.AppendLine($"  card blocks raw={HtmlTextUtilities.ExtractCardBlocks(raw, 500).Count}  stripped={HtmlTextUtilities.ExtractCardBlocks(stripped, 500).Count}");
        report.AppendLine();

        var anchorRegex = new Regex(@"<a\b([^>]*)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var rawAnchors = anchorRegex.Matches(raw).Select(m => (Attrs: m.Groups[1].Value, Text: HtmlTextUtilities.CleanInline(m.Groups[2].Value) ?? "")).ToList();
        var strippedAnchors = anchorRegex.Matches(stripped).Select(m => (Attrs: m.Groups[1].Value, Text: HtmlTextUtilities.CleanInline(m.Groups[2].Value) ?? "")).ToList();

        report.AppendLine($"--- anchors: raw={rawAnchors.Count}  stripped={strippedAnchors.Count} ---");
        report.AppendLine($"  'read more' anchors raw      : {rawAnchors.Count(a => a.Text.Contains("read more", StringComparison.OrdinalIgnoreCase))}");
        report.AppendLine($"  'read more' anchors stripped : {strippedAnchors.Count(a => a.Text.Contains("read more", StringComparison.OrdinalIgnoreCase))}");
        report.AppendLine($"  rel=bookmark raw             : {rawAnchors.Count(a => HtmlTextUtilities.AttributeValues(a.Attrs).Contains("bookmark"))}");
        report.AppendLine($"  entry-title/post-title raw   : {rawAnchors.Count(a => { var v = HtmlTextUtilities.AttributeValues(a.Attrs); return v.Contains("entry-title") || v.Contains("post-title"); })}");
        report.AppendLine();

        Uri.TryCreate(url, UriKind.Absolute, out var pageUri);

        report.AppendLine("--- first 20 candidate hrefs in RAW (pre-filter) ---");
        var seenHref = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shown = 0;
        foreach (var (attrs, text) in rawAnchors)
        {
            var href = HtmlTextUtilities.ReadAttribute(attrs, "href");
            if (href.Length == 0 || !seenHref.Add(href)) continue;

            var absolute = Absolute(href, pageUri);
            var verdict = absolute is null ? "UNRESOLVABLE" : (JobListingLinkExtractor.IsLikelyDetailUrl(absolute, pageUri) ? "ACCEPT" : $"REJECT ({RejectReason(absolute, pageUri)})");
            report.AppendLine($"  [{verdict,-28}] {Trim(href, 70)}   text=\"{Trim(text, 30)}\"");
            if (++shown >= 20) break;
        }
        report.AppendLine();

        report.AppendLine("--- accepted URLs across the WHOLE raw document ---");
        var accepted = new List<string>();
        var seenNorm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (attrs, _) in rawAnchors)
        {
            var absolute = Absolute(HtmlTextUtilities.ReadAttribute(attrs, "href"), pageUri);
            if (absolute is null || !JobListingLinkExtractor.IsLikelyDetailUrl(absolute, pageUri)) continue;
            if (!seenNorm.Add(JobListingLinkExtractor.NormalizeForComparison(absolute))) continue;
            accepted.Add(absolute);
        }
        report.AppendLine($"  total accepted+deduped: {accepted.Count}");
        foreach (var a in accepted.Take(25)) report.AppendLine($"    {a}");
        report.AppendLine();

        var analysis = new JobContentExtractor().Analyze(source, raw, fetch.ContentType, url);
        report.AppendLine("--- Analyze() result ---");
        report.AppendLine($"  kind      : {analysis.Kind}");
        report.AppendLine($"  links found (pre-cap) : {analysis.TotalChildLinksFound}");
        report.AppendLine($"  queued (post-cap)     : {analysis.ChildUrls.Count}  truncated={analysis.WasTruncatedByJobLimit}");
        report.AppendLine($"  nextPage  : {analysis.NextPageUrl ?? "(none)"}");
        foreach (var c in analysis.ChildUrls) report.AppendLine($"    {c}");
        foreach (var n in analysis.Notes) report.AppendLine($"  note: {n}");

        await File.WriteAllTextAsync(outputPath, report.ToString());
    }

    private static string? Absolute(string href, Uri? pageUri)
    {
        if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#')) return null;
        if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return null;

        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        if (pageUri is not null && Uri.TryCreate(pageUri, href, out var rel)) return rel.ToString();
        return null;
    }

    // Mirrors JobListingLinkExtractor.IsLikelyDetailUrl so a rejection
    // can be explained rather than just counted.
    private static string RejectReason(string url, Uri? pageUri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "unparseable";
        if (uri.Scheme is not ("http" or "https")) return "scheme";
        if (pageUri is not null && !string.Equals(uri.Host.Replace("www.", ""), pageUri.Host.Replace("www.", ""), StringComparison.OrdinalIgnoreCase))
            return $"off-site:{uri.Host}";

        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.Length <= 1) return "home page";

        foreach (var seg in new[] { "/category/", "/categories/", "/tag/", "/tags/", "/author/", "/page/", "/search",
                                     "/wp-admin", "/wp-login", "/wp-json", "/wp-content", "/feed", "/comments",
                                     "/privacy", "/disclaimer", "/terms", "/contact", "/about", "/sitemap", "/cookie",
                                     "/login", "/register", "/cart", "/checkout", "/my-account", "/advertise", "/faq",
                                     "/subscribe", "/newsletter", "/dmca", "/copyright" })
        {
            if (path.Contains(seg, StringComparison.Ordinal)) return $"excluded segment {seg}";
        }

        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico", ".pdf", ".zip", ".rar",
                                     ".doc", ".docx", ".xls", ".xlsx", ".css", ".js", ".xml", ".json", ".mp4", ".mp3" })
        {
            if (path.EndsWith(ext, StringComparison.Ordinal)) return $"asset {ext}";
        }

        if (Regex.IsMatch(path, @"/page/\d+/?$|^/\d+/?$")) return "pagination path";

        var query = uri.Query.ToLowerInvariant();
        foreach (var k in new[] { "paged=", "page=", "page_id=", "replytocom=" })
        {
            if (query.Contains(k, StringComparison.Ordinal)) return $"pagination query {k}";
        }

        var last = path.Trim('/').Split('/').LastOrDefault() ?? "";
        if (last.Length < 3) return $"slug too short '{last}'";

        return "unknown";
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}

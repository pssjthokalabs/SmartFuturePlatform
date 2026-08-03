using System.Text.RegularExpressions;

namespace SmartFuture.Application.Jobs.Import;

// Decides whether a fetched page is ONE job ad or an index of many, and
// when it is an index, returns the individual post URLs.
//
// Why this exists: WordPress archives (MyCareers' /category/jobs/,
// WorkJob's homepage) look like an article to a naive extractor — they
// have a <title>, thousands of words, and links. Treated as a detail
// page they produce a single garbage job whose title is the archive's
// title and whose description is the entire page including the sidebar.
// Classifying first, then visiting each child, is the only way to get
// one record per real vacancy.
//
// Everything here operates on markup that has already had its chrome
// removed by HtmlTextUtilities.StripBoilerplate, so a "recent posts"
// widget in the sidebar cannot masquerade as a page full of job cards.
public static partial class JobListingLinkExtractor
{
    // Three, not two. After boilerplate removal a genuine detail page
    // should have almost no heading links left in its content; the
    // realistic false positive is an in-content "related jobs" pair, and
    // requiring three clears it. Real archives carry ten or more.
    private const int MinimumListingLinks = 3;

    private const int MaxCandidateLinks = 200;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"<a\b([^>]*)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorWithAttributesRegex();

    // Path segments that are navigation, not vacancies. A link into any
    // of these is never a job detail page.
    private static readonly string[] ExcludedPathSegments =
    {
        "/category/", "/categories/", "/tag/", "/tags/", "/author/", "/page/", "/search",
        "/wp-admin", "/wp-login", "/wp-json", "/wp-content", "/feed", "/comments",
        "/privacy", "/disclaimer", "/terms", "/contact", "/about", "/sitemap", "/cookie",
        "/login", "/register", "/cart", "/checkout", "/my-account", "/advertise", "/faq",
        "/subscribe", "/newsletter", "/dmca", "/copyright"
    };

    private static readonly string[] ExcludedExtensions =
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico", ".pdf", ".zip", ".rar",
        ".doc", ".docx", ".xls", ".xlsx", ".css", ".js", ".xml", ".json", ".mp4", ".mp3"
    };

    private static readonly string[] PaginationQueryKeys = { "paged=", "page=", "page_id=", "replytocom=" };

    private static readonly string[] TitleLinkClassTokens =
    {
        "entry-title", "post-title", "job-title", "article-title", "more-link", "read-more"
    };

    public static JobPageAnalysis Analyze(string? html, string? pageUrl, int maxLinks)
    {
        var analysis = new JobPageAnalysis();
        if (string.IsNullOrWhiteSpace(html)) return analysis;

        Uri? pageUri = null;
        if (!string.IsNullOrWhiteSpace(pageUrl)) Uri.TryCreate(pageUrl, UriKind.Absolute, out pageUri);

        // Chrome-stripped first, because that is what keeps a sidebar's
        // "recent posts" widget from looking like a page of job cards.
        // But stripping is a heuristic over hostile markup, and when it
        // over-matches it can remove the very cards we are looking for —
        // so a page that yields nothing gets a second pass over the raw
        // document rather than being written off as "no jobs here".
        var stripped = HtmlTextUtilities.StripBoilerplate(html);
        var candidates = CollectCandidateLinks(stripped, pageUrl);

        if (candidates.Count == 0)
        {
            candidates = CollectCandidateLinks(html, pageUrl);
            if (candidates.Count > 0)
                analysis.Notes.Add("Chrome-stripped markup yielded no job links; links were read from the raw page instead.");
        }

        // Filter and dedupe EVERYTHING first, then truncate. Counting
        // before the cut is what lets the run log say "11 found, 5
        // imported" instead of silently reporting 5 of 5.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var qualifying = new List<string>();
        foreach (var url in candidates)
        {
            if (!IsLikelyDetailUrl(url, pageUri)) continue;
            if (!seen.Add(NormalizeForComparison(url))) continue;

            qualifying.Add(url);
            if (qualifying.Count >= MaxCandidateLinks) break;
        }

        analysis.TotalChildLinksFound = qualifying.Count;
        analysis.ChildUrls.AddRange(qualifying.Take(Math.Max(1, maxLinks)));

        if (qualifying.Count >= MinimumListingLinks)
        {
            analysis.Kind = JobPageKind.Listing;
            // Pagination lives in the chrome that StripBoilerplate
            // removes, so the next-page link is read from the original.
            analysis.NextPageUrl = FindNextPageUrl(html, pageUri);
            analysis.Notes.Add($"Page classified as a listing/archive page with {analysis.TotalChildLinksFound} job link(s).");
        }
        else
        {
            analysis.Kind = JobPageKind.Detail;
            analysis.ChildUrls.Clear();
            analysis.TotalChildLinksFound = 0;
        }

        return analysis;
    }

    // ─── Candidate collection ─────────────────────────────────────────

    private static List<string> CollectCandidateLinks(string strippedHtml, string? baseUrl)
    {
        var results = new List<string>();

        // Card-first: one link per <article>/.post-item block, which is
        // how a WordPress archive is actually laid out. Taking only the
        // first qualifying link per card stops a card's category and
        // comment links from each becoming a "job".
        foreach (var card in HtmlTextUtilities.ExtractCardBlocks(strippedHtml))
        {
            var link = FindPrimaryLinkInCard(card, baseUrl);
            if (link is not null) results.Add(link);
            if (results.Count >= MaxCandidateLinks) return results;
        }

        if (results.Count > 0) return results;

        // No cards — themes that render an index as bare headings.
        foreach (var heading in EnumerateHeadingBlocks(strippedHtml))
        {
            var link = FirstAnchor(heading, baseUrl, _ => true);
            if (link is not null) results.Add(link);
            if (results.Count >= MaxCandidateLinks) return results;
        }

        if (results.Count > 0) return results;

        // Bookmark/title-classed anchors anywhere in content.
        foreach (var (url, attrs, _) in EnumerateAnchors(strippedHtml, baseUrl))
        {
            if (!IsTitleLink(attrs)) continue;
            results.Add(url);
            if (results.Count >= MaxCandidateLinks) return results;
        }

        if (results.Count > 0) return results;

        // Last resort for themes that use none of the above: anchors
        // inside the page's own content region whose URL is shaped like
        // an article — a WordPress date path or a multi-word slug. The
        // URL filter still has the final say, so nav and category links
        // cannot get through here.
        var contentRegion = FindContentRegion(strippedHtml) ?? strippedHtml;
        foreach (var (url, _, _) in EnumerateAnchors(contentRegion, baseUrl))
        {
            if (!LooksLikeArticleUrl(url)) continue;
            results.Add(url);
            if (results.Count >= MaxCandidateLinks) break;
        }

        return results;
    }

    // main / #main / .site-main / .content — the containers themes use
    // for the post loop.
    private static string? FindContentRegion(string html)
    {
        var byTag = HtmlTextUtilities.ExtractElementsInner(html,
            (name, _) => string.Equals(name, "main", StringComparison.OrdinalIgnoreCase), limit: 1);
        if (byTag.Count > 0 && byTag[0].Length > 0) return byTag[0];

        var byClass = HtmlTextUtilities.ExtractElementsInner(html, (_, attrs) =>
        {
            var values = HtmlTextUtilities.AttributeValues(attrs);
            return values.Contains("site-main", StringComparison.Ordinal)
                || values.Contains("content-area", StringComparison.Ordinal)
                || values.Contains("main-content", StringComparison.Ordinal)
                || values == "content" || values.StartsWith("content ", StringComparison.Ordinal);
        }, limit: 1);

        return byClass.Count > 0 && byClass[0].Length > 0 ? byClass[0] : null;
    }

    // /2026/07/28/dsv-material-handler/ or /some-multi-word-slug/.
    [GeneratedRegex(@"/\d{4}/\d{1,2}/(?:\d{1,2}/)?[a-z0-9][a-z0-9\-]{4,}/?$", RegexOptions.IgnoreCase)]
    private static partial Regex DatedArticlePathRegex();

    [GeneratedRegex(@"/[a-z0-9]+(?:-[a-z0-9]+){2,}/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SlugArticlePathRegex();

    public static bool LooksLikeArticleUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var path = uri.AbsolutePath;
        try
        {
            return DatedArticlePathRegex().IsMatch(path) || SlugArticlePathRegex().IsMatch(path);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string? FindPrimaryLinkInCard(string card, string? baseUrl)
    {
        // Heading link is the strongest signal of "this card's post".
        foreach (var heading in EnumerateHeadingBlocks(card))
        {
            var fromHeading = FirstAnchor(heading, baseUrl, _ => true);
            if (fromHeading is not null) return fromHeading;
        }

        // Then rel=bookmark / .entry-title / .more-link, then a
        // "read more" by its text.
        var byAttribute = FirstAnchor(card, baseUrl, IsTitleLink);
        if (byAttribute is not null) return byAttribute;

        foreach (var (url, _, text) in EnumerateAnchors(card, baseUrl))
        {
            var normalized = text.Trim().ToLowerInvariant();
            if (normalized.StartsWith("read more", StringComparison.Ordinal)
                || normalized.StartsWith("view more", StringComparison.Ordinal)
                || normalized.StartsWith("apply now", StringComparison.Ordinal)
                || normalized.StartsWith("view job", StringComparison.Ordinal)
                || normalized.StartsWith("full details", StringComparison.Ordinal))
            {
                return url;
            }
        }

        return null;
    }

    private static bool IsTitleLink(string attrs)
    {
        var values = HtmlTextUtilities.AttributeValues(attrs);
        if (values.Length == 0) return false;

        if (values.Contains("bookmark", StringComparison.Ordinal)) return true;
        return TitleLinkClassTokens.Any(t => values.Contains(t, StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateHeadingBlocks(string html)
    {
        foreach (var tag in new[] { "h2", "h3", "h1", "h4" })
        {
            var name = tag;
            foreach (var inner in HtmlTextUtilities.ExtractElementsInner(html,
                (n, _) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase), limit: 100))
            {
                yield return inner;
            }
        }
    }

    private static string? FirstAnchor(string html, string? baseUrl, Func<string, bool> attributeFilter)
    {
        foreach (var (url, attrs, _) in EnumerateAnchors(html, baseUrl))
        {
            if (attributeFilter(attrs)) return url;
        }

        return null;
    }

    private static IEnumerable<(string Url, string Attrs, string Text)> EnumerateAnchors(string html, string? baseUrl)
    {
        Uri? baseUri = null;
        if (!string.IsNullOrWhiteSpace(baseUrl)) Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri);

        MatchCollection matches;
        try
        {
            matches = AnchorWithAttributesRegex().Matches(html);
        }
        catch (RegexMatchTimeoutException)
        {
            yield break;
        }

        foreach (Match match in matches)
        {
            var attrs = match.Groups[1].Value;
            var href = HtmlTextUtilities.ReadAttribute(attrs, "href");
            if (href.Length == 0 || href.StartsWith('#')) continue;
            if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;

            string absolute;
            if (Uri.TryCreate(href, UriKind.Absolute, out var direct))
            {
                if (direct.Scheme is not ("http" or "https")) continue;
                absolute = direct.ToString();
            }
            else if (baseUri is not null && Uri.TryCreate(baseUri, href, out var resolved))
            {
                absolute = resolved.ToString();
            }
            else
            {
                continue;
            }

            yield return (absolute, attrs, HtmlTextUtilities.CleanInline(match.Groups[2].Value) ?? string.Empty);
        }
    }

    // ─── URL filtering ────────────────────────────────────────────────

    public static bool IsLikelyDetailUrl(string? url, Uri? pageUri)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        // Off-site links are ads, socials and partner boards.
        if (pageUri is not null && !IsSameSite(uri, pageUri)) return false;

        var path = uri.AbsolutePath;

        // The site root and near-empty paths are the home page, not a job.
        if (path.Length <= 1) return false;

        var lowerPath = path.ToLowerInvariant();
        if (ExcludedPathSegments.Any(segment => lowerPath.Contains(segment, StringComparison.Ordinal))) return false;
        if (ExcludedExtensions.Any(ext => lowerPath.EndsWith(ext, StringComparison.Ordinal))) return false;

        // /page/2/ style pagination and ?paged=2 query pagination.
        if (PaginationRegex().IsMatch(lowerPath)) return false;

        var query = uri.Query.ToLowerInvariant();
        if (query.Length > 0 && PaginationQueryKeys.Any(k => query.Contains(k, StringComparison.Ordinal))) return false;

        // A slug of one or two characters is navigation furniture.
        var lastSegment = lowerPath.Trim('/').Split('/').LastOrDefault() ?? string.Empty;
        return lastSegment.Length >= 3;
    }

    [GeneratedRegex(@"/page/\d+/?$|^/\d+/?$")]
    private static partial Regex PaginationRegex();

    private static bool IsSameSite(Uri candidate, Uri page)
        => string.Equals(StripWww(candidate.Host), StripWww(page.Host), StringComparison.OrdinalIgnoreCase);

    private static string StripWww(string host)
        => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    // Compares URLs ignoring scheme, www, trailing slash and tracking
    // parameters, so the same post linked twice on one archive (title +
    // "read more") is imported once.
    public static string NormalizeForComparison(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim().ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{StripWww(uri.Host)}{path}".ToLowerInvariant();
    }

    // ─── Pagination ───────────────────────────────────────────────────

    public static string? FindNextPageUrl(string? html, Uri? pageUri)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        foreach (var (url, attrs, text) in EnumerateAnchors(html, pageUri?.ToString()))
        {
            var values = HtmlTextUtilities.AttributeValues(attrs);
            var normalized = text.Trim().ToLowerInvariant();

            var looksNext = values.Contains("next", StringComparison.Ordinal)
                || normalized == "next"
                || normalized.StartsWith("next ", StringComparison.Ordinal)
                || normalized == "›" || normalized == "»";

            if (!looksNext) continue;
            if (pageUri is not null && !IsSameSite(new Uri(url), pageUri)) continue;

            // Must actually be a different page from the one we're on.
            if (pageUri is not null && NormalizeForComparison(url) == NormalizeForComparison(pageUri.ToString())) continue;

            return url;
        }

        return null;
    }
}

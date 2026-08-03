using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartFuture.Application.Jobs.Import;

// Minimal, dependency-free HTML handling for the job importer.
//
// Why not an HTML parser package: the importer only needs four things —
// strip a document to readable text, pull <meta>/<title> values, collect
// the JSON-LD blocks, and sanitise a fragment before we store it. All
// four are safely expressible with bounded regexes over content we
// already treat as untrusted, and it keeps the deployment free of a new
// third-party dependency.
//
// Everything here is defensive: the input is arbitrary, possibly hostile
// markup from a third-party website. Nothing produced by this class is
// ever rendered without going through SanitizeHtml first.
public static partial class HtmlTextUtilities
{
    // 2 seconds is generous for these patterns; the timeout exists so a
    // pathological page cannot stall an import run.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"<(script|style|noscript|svg|iframe)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleBlockRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</li>|</tr>|</h[1-6]>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex H1Regex();

    [GeneratedRegex(@"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonLdRegex();

    [GeneratedRegex(@"<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    // Event-handler attributes (onclick=…) and javascript: URLs — the two
    // ways a stored fragment could execute in an admin's browser.
    [GeneratedRegex(@"\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineEventHandlerRegex();

    [GeneratedRegex(@"(href|src)\s*=\s*([""']?)\s*javascript:[^""'\s>]*\2", RegexOptions.IgnoreCase)]
    private static partial Regex JavascriptUrlRegex();

    [GeneratedRegex(@"[ \t\f\v]{2,}")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"(\r?\n\s*){3,}")]
    private static partial Regex ExcessNewlineRegex();

    // Flattens a document (or fragment) to readable plain text.
    // Block-level closers become newlines so bullet lists and paragraphs
    // survive as separate lines — important because the requirement
    // extractors below work line by line.
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        try
        {
            var working = ScriptStyleBlockRegex().Replace(html, " ");
            working = CommentRegex().Replace(working, " ");
            working = BlockBreakRegex().Replace(working, "\n");
            working = AnyTagRegex().Replace(working, " ");
            working = WebUtility.HtmlDecode(working);
            working = HorizontalWhitespaceRegex().Replace(working, " ");
            working = ExcessNewlineRegex().Replace(working, "\n\n");

            // Trim each line so indentation from the source markup
            // doesn't leak into stored text.
            var lines = working.Split('\n').Select(l => l.Trim());
            return string.Join('\n', lines).Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            // Give up on structure and fall back to a crude strip rather
            // than failing the whole import.
            return WebUtility.HtmlDecode(AnyTagRegex().Replace(html, " ")).Trim();
        }
    }

    // Removes the executable surface from a fragment we intend to STORE
    // and later render (DescriptionHtml). Conservative by design: script/
    // style/iframe blocks, comments, inline handlers, and javascript:
    // URLs all go. Presentational markup (p, ul, li, strong…) is kept so
    // the description still reads like a job ad.
    public static string? SanitizeHtml(string? html, int maxLength = 60000)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        try
        {
            var working = ScriptStyleBlockRegex().Replace(html, string.Empty);
            working = CommentRegex().Replace(working, string.Empty);
            working = InlineEventHandlerRegex().Replace(working, string.Empty);
            working = JavascriptUrlRegex().Replace(working, string.Empty);
            working = working.Trim();

            if (working.Length > maxLength) working = working[..maxLength];
            return working.Length == 0 ? null : working;
        }
        catch (RegexMatchTimeoutException)
        {
            // If we can't prove it's clean, don't store markup at all.
            return null;
        }
    }

    public static string? ExtractTitle(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        // og:title first — it is the page author's own idea of the
        // headline and is usually free of the "| Site Name" suffix.
        var og = ExtractMetaContent(html, "og:title");
        if (!string.IsNullOrWhiteSpace(og)) return CleanInline(og);

        var h1 = MatchGroup(H1Regex(), html);
        if (!string.IsNullOrWhiteSpace(h1)) return CleanInline(h1);

        var title = MatchGroup(TitleRegex(), html);
        return string.IsNullOrWhiteSpace(title) ? null : CleanInline(title);
    }

    // Handles both attribute orders: <meta name=… content=…> and
    // <meta content=… name=…>, and both `name` and `property`.
    public static string? ExtractMetaContent(string? html, string metaName)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(metaName)) return null;

        var escaped = Regex.Escape(metaName);
        var patterns = new[]
        {
            $@"<meta[^>]*(?:name|property)\s*=\s*[""']{escaped}[""'][^>]*content\s*=\s*[""']([^""']*)[""'][^>]*>",
            $@"<meta[^>]*content\s*=\s*[""']([^""']*)[""'][^>]*(?:name|property)\s*=\s*[""']{escaped}[""'][^>]*>"
        };

        foreach (var pattern in patterns)
        {
            try
            {
                var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
                if (match.Success) return CleanInline(match.Groups[1].Value);
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        return null;
    }

    // Returns the raw JSON payload of every <script type="application/ld+json">
    // block. Callers parse and filter for @type == JobPosting.
    public static IReadOnlyList<string> ExtractJsonLdBlocks(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<string>();

        try
        {
            return JsonLdRegex().Matches(html)
                .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value).Trim())
                .Where(v => v.Length > 0)
                .Take(50)
                .ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return Array.Empty<string>();
        }
    }

    // (absoluteUrl, linkText) pairs. Relative hrefs are resolved against
    // baseUrl so a listing page's links are followable.
    public static IReadOnlyList<(string Url, string Text)> ExtractLinks(string? html, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<(string, string)>();

        Uri? baseUri = null;
        if (!string.IsNullOrWhiteSpace(baseUrl)) Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri);

        var results = new List<(string, string)>();
        try
        {
            foreach (Match match in AnchorRegex().Matches(html))
            {
                var href = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
                if (href.Length == 0 || href.StartsWith('#')) continue;
                if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;

                var text = CleanInline(match.Groups[2].Value) ?? string.Empty;

                if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
                {
                    if (absolute.Scheme is not ("http" or "https") && !href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                    results.Add((absolute.ToString(), text));
                }
                else if (baseUri is not null && Uri.TryCreate(baseUri, href, out var resolved))
                {
                    results.Add((resolved.ToString(), text));
                }

                if (results.Count >= 500) break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Return whatever we collected before the timeout.
        }

        return results;
    }

    // Strips tags + decodes entities for a short inline value (a title,
    // a meta content string).
    public static string? CleanInline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var text = WebUtility.HtmlDecode(AnyTagRegex().Replace(value, " "));
            return JobTextUtilities.CollapseWhitespace(text) is { Length: > 0 } cleaned ? cleaned : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    // Cap what we keep as RawContentSnapshot — enough to debug a bad
    // extraction, not enough to bloat the table.
    public static string? Snapshot(string? content, int maxLength = 20000)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var trimmed = content.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    // First capture group of the first match, or null. Regex timeouts
    // degrade to "no match" rather than propagating into an import run.
    private static string? MatchGroup(Regex regex, string input)
    {
        try
        {
            var match = regex.Match(input);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    public static string BuildTextSnippet(IEnumerable<string> lines, int maxLength)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > maxLength) break;
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }

    // ─── Boilerplate removal ──────────────────────────────────────────
    //
    // Everything below exists because a WordPress page is mostly NOT the
    // article. Nav, sidebar, tag cloud, "recent posts", the push-notify
    // prompt and the footer contact block are all real text that used to
    // land in DescriptionText — and the footer's contact address used to
    // become the apply email. Stripping them first is what makes the
    // salary/email/apply-link heuristics trustworthy, so this runs before
    // any field parsing on a detail page.

    // Removed wholesale by tag name. <header> is deliberately NOT here:
    // on an archive card the post title link lives inside <header
    // class="entry-header">, so removing it would delete the very links
    // the listing crawler is looking for. Site chrome is caught by the
    // class tokens below instead.
    private static readonly string[] BoilerplateTagNames = { "nav", "footer", "aside" };

    // Matched as substrings against a tag's class/id/role. Short or
    // ambiguous words ("ads", "top") are excluded on purpose — a false
    // positive here silently deletes job content.
    private static readonly string[] BoilerplateClassTokens =
    {
        "sidebar", "widget", "comment", "breadcrumb", "pagination", "page-numbers", "navigation", "menu",
        "webpushr", "push-notification", "onesignal", "newsletter", "subscribe", "related", "social",
        "share", "cookie", "popup", "modal", "advert", "masthead", "colophon", "tagcloud", "tag-cloud",
        "recent-post", "popular-post", "author-box", "back-to-top", "skip-link", "site-header",
        "site-footer", "screen-reader", "offcanvas", "search-form", "site-branding", "post-nav"
    };

    // Structural elements are NEVER removed, whatever their class says.
    //
    // WordPress themes advertise layout on <body>: WorkJob ships
    // class="… has-site-branding has-right-sidebar", and "has-right-
    // sidebar" contains "sidebar". That matched, the whole <body> was
    // deleted, and a homepage with eleven job cards extracted zero
    // links. A class on <body> describes the page; it never means the
    // page IS chrome.
    private static readonly string[] NeverRemovedTagNames = { "html", "body", "main" };

    // Second belt: chrome is never most of the document. If a match
    // would delete more than this share of the page, the selector is
    // wrong — keep the content and let the field parsers cope.
    private const double MaxRemovalFractionOfDocument = 0.5;

    // A hostile or merely bloated page shouldn't let the scanner run
    // unbounded.
    private const int MaxElementRemovals = 500;

    // Content containers, best-first. The first one that exists wins.
    private static readonly string[] ContentClassTokens =
    {
        "entry-content", "post-content", "article-content", "job-description", "single-post", "the-content"
    };

    // Returns the document with script/style/comments and site chrome
    // removed. Structure is otherwise preserved, so the result is still
    // valid input for link extraction and ToPlainText.
    public static string StripBoilerplate(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        try
        {
            var working = ScriptStyleBlockRegex().Replace(html, " ");
            working = CommentRegex().Replace(working, " ");

            return RemoveMatchingElements(working, (tag, attrs) =>
                !NeverRemovedTagNames.Contains(tag, StringComparer.OrdinalIgnoreCase)
                && (BoilerplateTagNames.Contains(tag, StringComparer.OrdinalIgnoreCase) || HasBoilerplateToken(attrs)));
        }
        catch (RegexMatchTimeoutException)
        {
            return html;
        }
    }

    // The readable body of a DETAIL page: the first recognised content
    // container, else the first <article>/<main>. Returns null when the
    // page has no such container and the caller should fall back to the
    // whole (already de-chromed) document.
    public static string? ExtractMainContent(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var byClass = ExtractElementsInner(html,
            (_, attrs) => ContentClassTokens.Any(t => AttributeValues(attrs).Contains(t, StringComparison.Ordinal)), limit: 1);
        if (byClass.Count > 0 && byClass[0].Length > 0) return byClass[0];

        foreach (var tag in new[] { "article", "main" })
        {
            var byTag = ExtractElementsInner(html, (name, _) => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase), limit: 1);
            if (byTag.Count > 0 && byTag[0].Length > 0) return byTag[0];
        }

        return null;
    }

    // The repeated card elements of an ARCHIVE page — <article> plus the
    // class-based equivalents themes use when they don't emit <article>.
    public static IReadOnlyList<string> ExtractCardBlocks(string? html, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<string>();

        var articles = ExtractElementsInner(html,
            (name, _) => string.Equals(name, "article", StringComparison.OrdinalIgnoreCase), limit);
        if (articles.Count > 0) return articles;

        return ExtractElementsInner(html, (_, attrs) =>
        {
            var values = AttributeValues(attrs);
            return values.Contains("post-item", StringComparison.Ordinal)
                || values.Contains("type-post", StringComparison.Ordinal)
                || values.Contains("hentry", StringComparison.Ordinal)
                || values.Contains("job-listing", StringComparison.Ordinal)
                || values.Contains("job-item", StringComparison.Ordinal)
                || values.Contains("entry-item", StringComparison.Ordinal)
                || values.Contains("loop-item", StringComparison.Ordinal);
        }, limit);
    }

    // Inner HTML of every element whose (tagName, attributes) satisfy the
    // predicate. Nested matches are not re-entered — an outer <article>
    // consumes its children, which is what card extraction wants.
    public static IReadOnlyList<string> ExtractElementsInner(string? html, Func<string, string, bool> match, int limit = 50)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(html) || limit <= 0) return results;

        var i = 0;
        while (i < html.Length && results.Count < limit)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) break;

            var gt = html.IndexOf('>', lt);
            if (gt < 0) break;

            if (!TryReadOpenTag(html, lt, gt, out var tagName, out var attrs))
            {
                i = gt + 1;
                continue;
            }

            if (match(tagName, attrs))
            {
                var closeLt = FindMatchingCloseIndex(html, tagName, gt + 1);
                if (closeLt > gt)
                {
                    results.Add(html[(gt + 1)..closeLt]);
                    var closeGt = html.IndexOf('>', closeLt);
                    i = closeGt < 0 ? html.Length : closeGt + 1;
                    continue;
                }
            }

            i = gt + 1;
        }

        return results;
    }

    // Diagnostic: what StripBoilerplate would delete, and how big each
    // piece is. Exists because a single over-broad match can silently
    // remove the entire body of a page, and counting characters after
    // the fact tells you nothing about which selector did it.
    public static IReadOnlyList<string> DescribeBoilerplateRemovals(string? html)
    {
        var removals = new List<string>();
        if (string.IsNullOrWhiteSpace(html)) return removals;

        var working = ScriptStyleBlockRegex().Replace(html, " ");
        working = CommentRegex().Replace(working, " ");

        RemoveMatchingElements(working,
            (tag, attrs) => !NeverRemovedTagNames.Contains(tag, StringComparer.OrdinalIgnoreCase)
                && (BoilerplateTagNames.Contains(tag, StringComparer.OrdinalIgnoreCase) || HasBoilerplateToken(attrs)),
            removals);

        return removals;
    }

    private static string RemoveMatchingElements(string html, Func<string, string, bool> shouldRemove, List<string>? removalLog = null)
    {
        var sb = new StringBuilder(html.Length);
        var i = 0;
        var removals = 0;

        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }

            sb.Append(html, i, lt - i);

            var gt = html.IndexOf('>', lt);
            if (gt < 0)
            {
                sb.Append(html, lt, html.Length - lt);
                break;
            }

            if (removals < MaxElementRemovals
                && TryReadOpenTag(html, lt, gt, out var tagName, out var attrs)
                && shouldRemove(tagName, attrs))
            {
                var closeLt = FindMatchingCloseIndex(html, tagName, gt + 1);
                if (closeLt > gt && !IsTooLargeToBeChrome(closeLt - lt, html.Length))
                {
                    var closeGt = html.IndexOf('>', closeLt);
                    removalLog?.Add($"<{tagName}> len={closeLt - lt} attrs={Truncate(attrs.Trim(), 90)}");
                    i = closeGt < 0 ? html.Length : closeGt + 1;
                    removals++;
                    // Keep a separator so removal can't fuse two words.
                    sb.Append(' ');
                    continue;
                }

                if (closeLt > gt)
                    removalLog?.Add($"SKIPPED <{tagName}> len={closeLt - lt} (over {MaxRemovalFractionOfDocument:P0} of document) attrs={Truncate(attrs.Trim(), 90)}");
            }

            sb.Append(html, lt, gt - lt + 1);
            i = gt + 1;
        }

        return sb.ToString();
    }

    // True only for an opening element tag; closing tags, comments,
    // doctypes and processing instructions are rejected.
    private static bool TryReadOpenTag(string html, int lt, int gt, out string tagName, out string attrs)
    {
        tagName = string.Empty;
        attrs = string.Empty;

        var start = lt + 1;
        if (start >= gt) return false;

        var c = html[start];
        if (c is '/' or '!' or '?') return false;

        var nameEnd = start;
        while (nameEnd < gt && (char.IsLetterOrDigit(html[nameEnd]) || html[nameEnd] == '-')) nameEnd++;
        if (nameEnd == start) return false;

        tagName = html[start..nameEnd];
        attrs = html[nameEnd..gt];
        return true;
    }

    // Index of the '<' of the close tag matching an already-opened
    // element, honouring nesting. -1 when the document is unbalanced.
    private static int FindMatchingCloseIndex(string html, string tagName, int searchFrom)
    {
        var depth = 1;
        var i = searchFrom;

        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) return -1;

            var gt = html.IndexOf('>', lt);
            if (gt < 0) return -1;

            var isClosing = lt + 1 < html.Length && html[lt + 1] == '/';
            var nameStart = isClosing ? lt + 2 : lt + 1;

            if (IsTagNameAt(html, nameStart, tagName, gt))
            {
                if (isClosing)
                {
                    depth--;
                    if (depth == 0) return lt;
                }
                else if (html[gt - 1] != '/')
                {
                    depth++;
                }
            }

            i = gt + 1;
        }

        return -1;
    }

    private static bool IsTagNameAt(string html, int index, string tagName, int tagEnd)
    {
        if (index + tagName.Length > tagEnd) return false;
        if (string.Compare(html, index, tagName, 0, tagName.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;

        var after = index + tagName.Length;
        if (after >= tagEnd) return true;

        var c = html[after];
        return !char.IsLetterOrDigit(c) && c != '-';
    }

    private static bool HasBoilerplateToken(string attrs)
    {
        if (attrs.Length == 0) return false;
        var values = AttributeValues(attrs);
        if (values.Length == 0) return false;

        return BoilerplateClassTokens.Any(token => values.Contains(token, StringComparison.Ordinal));
    }

    // Lower-cased class + id + role of one tag, as a single haystack.
    public static string AttributeValues(string attrs)
    {
        if (string.IsNullOrWhiteSpace(attrs)) return string.Empty;

        var sb = new StringBuilder();
        foreach (var name in new[] { "class", "id", "role", "rel" })
        {
            var value = ReadAttribute(attrs, name);
            if (value.Length > 0) sb.Append(value).Append(' ');
        }

        return sb.ToString().ToLowerInvariant();
    }

    private static bool IsTooLargeToBeChrome(int elementLength, int documentLength)
        => documentLength > 0 && elementLength > documentLength * MaxRemovalFractionOfDocument;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    public static string ReadAttribute(string attrs, string name)
    {
        if (string.IsNullOrWhiteSpace(attrs)) return string.Empty;

        try
        {
            var match = Regex.Match(attrs, name + @"\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))",
                RegexOptions.IgnoreCase, RegexTimeout);
            if (!match.Success) return string.Empty;

            for (var g = 2; g <= 4; g++)
            {
                if (match.Groups[g].Success) return WebUtility.HtmlDecode(match.Groups[g].Value).Trim();
            }

            return string.Empty;
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }
}

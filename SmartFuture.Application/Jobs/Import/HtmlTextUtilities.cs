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
}

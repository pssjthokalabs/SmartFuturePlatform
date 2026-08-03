using System.Text.Json;
using System.Xml.Linq;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Import;

// Three extraction strategies, tried strongest-first for HTML pages:
//
//   1. JSON-LD schema.org/JobPosting — structured, authoritative, and
//      what every serious job board publishes for Google for Jobs.
//   2. RSS / Atom — used when the source is configured as a feed.
//   3. Article fallback — the page is treated as ONE grouped opportunity
//      with its readable text preserved. This is what handles text-heavy
//      posts (the Workjob-style article that lists several roles in
//      prose): rather than mis-splitting the roles and producing
//      garbage, v1 imports the article intact with structured sections
//      and the application instructions, and keeps the original URL
//      visible so the reader can see every role in context.
public class JobContentExtractor : IJobContentExtractor
{
    // Below this, the "article" isn't a job ad — it's a nav page or an
    // error/anti-bot interstitial.
    private const int MinimumArticleLength = 400;

    public JobExtractionResult Extract(JobSource source, string content, string? contentType, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(content))
            return JobExtractionResult.Empty("Source returned an empty body.");

        return source.SourceType switch
        {
            JobSourceType.RssFeed => ExtractFromFeed(source, content, nowUtc),
            JobSourceType.Api => JobExtractionResult.Empty(
                "Api source type has no extractor yet. Configure the source as RssFeed or HtmlPage, or capture its jobs manually."),
            JobSourceType.Manual => JobExtractionResult.Empty("Manual sources are never crawled."),
            _ => ExtractFromHtml(source, content, nowUtc)
        };
    }

    // ─── HTML ─────────────────────────────────────────────────────────

    private JobExtractionResult ExtractFromHtml(JobSource source, string html, DateTime nowUtc)
    {
        var result = new JobExtractionResult();

        // A feed served with an HTML-ish content type still parses as
        // XML — try it before giving up on structure.
        if (LooksLikeXmlFeed(html))
        {
            var feed = ExtractFromFeed(source, html, nowUtc);
            if (feed.Jobs.Count > 0)
            {
                feed.Notes.Insert(0, "Source is configured as HtmlPage but served an RSS/Atom feed; parsed as a feed.");
                return feed;
            }
        }

        var jsonLdJobs = ExtractJsonLdJobPostings(source, html, nowUtc, result.Notes);
        if (jsonLdJobs.Count > 0)
        {
            result.Jobs.AddRange(jsonLdJobs);
            result.StrategyUsed = "json-ld";
            result.Notes.Add($"Extracted {jsonLdJobs.Count} job(s) from schema.org JobPosting JSON-LD.");
            return result;
        }

        var article = ExtractArticle(source, html, nowUtc, result.Notes);
        if (article is not null)
        {
            result.Jobs.Add(article);
            result.StrategyUsed = "article";
            return result;
        }

        result.StrategyUsed = "none";
        return result;
    }

    private static bool LooksLikeXmlFeed(string content)
    {
        var head = content.TrimStart();
        if (head.Length > 400) head = head[..400];
        return head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<rss", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<feed", StringComparison.OrdinalIgnoreCase);
    }

    // ─── Strategy 1: schema.org JobPosting via JSON-LD ────────────────

    private static List<ExtractedJob> ExtractJsonLdJobPostings(JobSource source, string html, DateTime nowUtc, List<string> notes)
    {
        var jobs = new List<ExtractedJob>();

        foreach (var block in HtmlTextUtilities.ExtractJsonLdBlocks(html))
        {
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(block);
            }
            catch (JsonException)
            {
                notes.Add("Skipped a malformed JSON-LD block.");
                continue;
            }

            using (document)
            {
                foreach (var element in EnumerateJsonLdNodes(document.RootElement))
                {
                    if (!IsJobPosting(element)) continue;

                    var job = MapJsonLdJobPosting(element, source, nowUtc);
                    if (job is not null) jobs.Add(job);
                    if (jobs.Count >= source.MaxJobsPerRun) return jobs;
                }
            }
        }

        return jobs;
    }

    // JSON-LD may be a single object, an array, or an @graph wrapper.
    private static IEnumerable<JsonElement> EnumerateJsonLdNodes(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in root.EnumerateArray())
                {
                    foreach (var nested in EnumerateJsonLdNodes(item)) yield return nested;
                }
                break;

            case JsonValueKind.Object:
                yield return root;
                if (root.TryGetProperty("@graph", out var graph))
                {
                    foreach (var nested in EnumerateJsonLdNodes(graph)) yield return nested;
                }
                break;
        }
    }

    private static bool IsJobPosting(JsonElement element)
    {
        if (!element.TryGetProperty("@type", out var type)) return false;

        if (type.ValueKind == JsonValueKind.String)
            return string.Equals(type.GetString(), "JobPosting", StringComparison.OrdinalIgnoreCase);

        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String
                && string.Equals(t.GetString(), "JobPosting", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static ExtractedJob? MapJsonLdJobPosting(JsonElement node, JobSource source, DateTime nowUtc)
    {
        var title = HtmlTextUtilities.CleanInline(ReadString(node, "title"));
        if (string.IsNullOrWhiteSpace(title)) return null;

        var descriptionHtml = ReadString(node, "description");
        var descriptionText = HtmlTextUtilities.ToPlainText(descriptionHtml);

        var location = ReadJobLocation(node) ?? source.DefaultLocation;
        var (loc, city, province) = JobFieldParsers.ParseLocation(location);

        var employmentType = HtmlTextUtilities.CleanInline(ReadStringOrFirst(node, "employmentType"));
        var applyUrl = ReadString(node, "url") ?? ReadString(node, "@id");

        var job = new ExtractedJob
        {
            Title = title,
            CompanyName = ReadHiringOrganization(node),
            Location = loc,
            City = city,
            Province = province,
            Country = ReadJobCountry(node) ?? "South Africa",
            Category = HtmlTextUtilities.CleanInline(ReadStringOrFirst(node, "occupationalCategory"))
                ?? source.DefaultCategory
                ?? JobFieldParsers.InferCategory(title, descriptionText),
            EmploymentType = JobFieldParsers.ParseEmploymentType(employmentType) ?? employmentType,
            SalaryText = ReadSalary(node),
            DescriptionHtml = HtmlTextUtilities.SanitizeHtml(descriptionHtml),
            DescriptionText = descriptionText,
            RequirementsText = HtmlTextUtilities.CleanInline(ReadString(node, "qualifications"))
                ?? JobFieldParsers.ExtractRequirements(descriptionText),
            ApplyUrl = applyUrl,
            SourceUrl = applyUrl ?? source.SourceUrl,
            ExternalId = ReadString(node, "identifier") ?? ReadIdentifierObject(node),
            PostedDateUtc = JobFieldParsers.ParseDate(ReadString(node, "datePosted")),
            ClosingDateUtc = JobFieldParsers.ParseDate(ReadString(node, "validThrough")),
            LogoUrl = ReadLogo(node),
            ExtractionStrategy = "json-ld"
        };

        job.WorkplaceType = ResolveWorkplaceType(node, job);
        job.Summary = JobTextUtilities.BuildSummary(job.DescriptionText);
        job.ApplyEmail = JobFieldParsers.ExtractEmail(job.DescriptionText);
        job.ApplicationInstructions = BuildApplicationInstructions(job);

        // Trust an explicit validThrough even if it's already in the
        // past — the importer marks it expired, which is correct.
        if (job.ClosingDateUtc is null)
            job.ClosingDateUtc = JobFieldParsers.ExtractClosingDate(job.DescriptionText, nowUtc);

        return job;
    }

    private static JobWorkplaceType ResolveWorkplaceType(JsonElement node, ExtractedJob job)
    {
        // schema.org marks fully-remote roles with jobLocationType =
        // TELECOMMUTE. That is authoritative when present.
        var locationType = ReadString(node, "jobLocationType");
        if (!string.IsNullOrWhiteSpace(locationType) && locationType.Contains("TELECOMMUTE", StringComparison.OrdinalIgnoreCase))
            return JobWorkplaceType.Remote;

        var fromText = JobFieldParsers.ParseWorkplaceType($"{job.Title} {job.Location} {job.DescriptionText}");
        return fromText;
    }

    // ─── Strategy 2: RSS / Atom ───────────────────────────────────────

    private static JobExtractionResult ExtractFromFeed(JobSource source, string xml, DateTime nowUtc)
    {
        var result = new JobExtractionResult { StrategyUsed = "rss" };

        XDocument document;
        try
        {
            // DTD processing stays off (the XDocument default) so a
            // hostile feed can't pull an external entity.
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException ex)
        {
            return JobExtractionResult.Empty($"Feed is not valid XML: {ex.Message}");
        }

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var items = document.Descendants("item").Concat(document.Descendants(atom + "entry")).Take(source.MaxJobsPerRun).ToList();

        if (items.Count == 0)
        {
            result.Notes.Add("Feed parsed but contained no <item> or <entry> elements.");
            return result;
        }

        foreach (var item in items)
        {
            var title = HtmlTextUtilities.CleanInline(ReadElement(item, "title", atom));
            if (string.IsNullOrWhiteSpace(title))
            {
                result.Notes.Add("Skipped a feed item with no title.");
                continue;
            }

            var link = ReadElement(item, "link", atom) ?? ReadAtomLinkHref(item, atom);
            var rawDescription = ReadElement(item, "description", atom)
                ?? ReadElement(item, "summary", atom)
                ?? ReadElement(item, "content", atom)
                ?? ReadContentEncoded(item);

            var descriptionText = HtmlTextUtilities.ToPlainText(rawDescription);
            var (loc, city, province) = JobFieldParsers.ParseLocation(source.DefaultLocation);

            var job = new ExtractedJob
            {
                Title = title,
                Location = loc,
                City = city,
                Province = province,
                Country = "South Africa",
                Category = source.DefaultCategory ?? JobFieldParsers.InferCategory(title, descriptionText),
                DescriptionHtml = HtmlTextUtilities.SanitizeHtml(rawDescription),
                DescriptionText = descriptionText,
                RequirementsText = JobFieldParsers.ExtractRequirements(descriptionText),
                SalaryText = JobFieldParsers.ExtractSalary(descriptionText),
                EmploymentType = JobFieldParsers.ParseEmploymentType($"{title} {descriptionText}"),
                WorkplaceType = JobFieldParsers.ParseWorkplaceType($"{title} {descriptionText}"),
                ApplyUrl = link,
                SourceUrl = link ?? source.SourceUrl,
                ExternalId = ReadElement(item, "guid", atom) ?? ReadElement(item, "id", atom),
                PostedDateUtc = JobFieldParsers.ParseDate(ReadElement(item, "pubDate", atom) ?? ReadElement(item, "updated", atom) ?? ReadElement(item, "published", atom)),
                ClosingDateUtc = JobFieldParsers.ExtractClosingDate(descriptionText, nowUtc),
                RawContentSnapshot = HtmlTextUtilities.Snapshot(item.ToString()),
                ExtractionStrategy = "rss"
            };

            job.Summary = JobTextUtilities.BuildSummary(job.DescriptionText);
            job.ApplyEmail = JobFieldParsers.ExtractEmail(job.DescriptionText);
            job.ApplicationInstructions = BuildApplicationInstructions(job);

            result.Jobs.Add(job);
        }

        return result;
    }

    // ─── Strategy 3: article fallback ─────────────────────────────────
    //
    // The page becomes ONE grouped opportunity. Deliberate v1 choice:
    // splitting an article that lists several roles in prose is where
    // naive crawlers produce nonsense titles and orphaned requirements.
    // A single, correctly-attributed entry with the full text and a
    // visible source link is more useful than five wrong ones — and the
    // admin can split it by hand from the portal if it is worth it.

    private static ExtractedJob? ExtractArticle(JobSource source, string html, DateTime nowUtc, List<string> notes)
    {
        var title = HtmlTextUtilities.ExtractTitle(html);
        var bodyText = HtmlTextUtilities.ToPlainText(html);

        if (string.IsNullOrWhiteSpace(title))
        {
            notes.Add("Could not determine a title for the page; nothing imported.");
            return null;
        }

        if (bodyText.Length < MinimumArticleLength)
        {
            notes.Add($"Page body was too short to be a job listing ({bodyText.Length} chars). It may be an anti-bot or error page.");
            return null;
        }

        var (loc, city, province) = JobFieldParsers.ParseLocation(
            source.DefaultLocation ?? FindLocationInText(bodyText));

        var applyEmail = JobFieldParsers.ExtractEmail(bodyText);
        var applyUrl = FindApplyLink(html, source.SourceUrl);

        var job = new ExtractedJob
        {
            Title = title,
            CompanyName = HtmlTextUtilities.ExtractMetaContent(html, "og:site_name"),
            Location = loc,
            City = city,
            Province = province,
            Country = "South Africa",
            Category = source.DefaultCategory ?? JobFieldParsers.InferCategory(title, bodyText),
            EmploymentType = JobFieldParsers.ParseEmploymentType($"{title} {bodyText}"),
            WorkplaceType = JobFieldParsers.ParseWorkplaceType($"{title} {bodyText}"),
            SalaryText = JobFieldParsers.ExtractSalary(bodyText),
            Summary = HtmlTextUtilities.ExtractMetaContent(html, "og:description")
                ?? HtmlTextUtilities.ExtractMetaContent(html, "description")
                ?? JobTextUtilities.BuildSummary(bodyText),
            DescriptionText = bodyText.Length > 40000 ? bodyText[..40000] : bodyText,
            RequirementsText = JobFieldParsers.ExtractRequirements(bodyText),
            ApplyUrl = applyUrl,
            ApplyEmail = applyEmail,
            SourceUrl = source.SourceUrl,
            PostedDateUtc = JobFieldParsers.ExtractPostedDate(bodyText, nowUtc)
                ?? JobFieldParsers.ParseDate(HtmlTextUtilities.ExtractMetaContent(html, "article:published_time")),
            ClosingDateUtc = JobFieldParsers.ExtractClosingDate(bodyText, nowUtc),
            LogoUrl = HtmlTextUtilities.ExtractMetaContent(html, "og:image"),
            RawContentSnapshot = HtmlTextUtilities.Snapshot(bodyText),
            ExtractionStrategy = "article"
        };

        job.ApplicationInstructions = BuildApplicationInstructions(job);

        if (job.ApplyUrl is null && job.ApplyEmail is null)
            notes.Add("No apply link or email found; the listing points back to its original source URL.");

        return job;
    }

    // Prefer a link whose text or href actually says "apply".
    private static string? FindApplyLink(string html, string? baseUrl)
    {
        foreach (var (url, text) in HtmlTextUtilities.ExtractLinks(html, baseUrl))
        {
            if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;

            var haystack = $"{text} {url}".ToLowerInvariant();
            if (haystack.Contains("apply", StringComparison.Ordinal)
                || haystack.Contains("submit cv", StringComparison.Ordinal)
                || haystack.Contains("application form", StringComparison.Ordinal))
            {
                return url;
            }
        }

        return null;
    }

    // Scan the first part of the body for a recognisable SA location.
    private static string? FindLocationInText(string bodyText)
    {
        var head = bodyText.Length > 3000 ? bodyText[..3000] : bodyText;
        foreach (var line in head.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length is 0 or > 120) continue;

            var (_, city, province) = JobFieldParsers.ParseLocation(trimmed);
            if (city is not null || province is not null) return trimmed;
        }

        return null;
    }

    // ─── Shared ───────────────────────────────────────────────────────

    // Always produce SOMETHING actionable. An email-only ad (very common
    // on SA boards) gets an explicit "send your CV to …" instruction and
    // a mailto link; otherwise the original source URL is the fallback.
    private static string? BuildApplicationInstructions(ExtractedJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.ApplicationInstructions)) return job.ApplicationInstructions;

        if (!string.IsNullOrWhiteSpace(job.ApplyEmail))
        {
            var subject = string.IsNullOrWhiteSpace(job.Title) ? "Job application" : $"Application: {job.Title}";
            return $"Email your CV to {job.ApplyEmail} (mailto:{job.ApplyEmail}?subject={Uri.EscapeDataString(subject)}). "
                + "Quote the job title in your subject line.";
        }

        if (!string.IsNullOrWhiteSpace(job.ApplyUrl))
            return "Apply online using the link provided on this listing.";

        if (!string.IsNullOrWhiteSpace(job.SourceUrl))
            return "Full application details are available on the original listing — use the source link on this page.";

        return null;
    }

    // ─── JSON-LD readers ──────────────────────────────────────────────

    private static string? ReadString(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? JobTextUtilities.NullIfBlank(value.GetString()) : null;
    }

    private static string? ReadStringOrFirst(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => JobTextUtilities.NullIfBlank(value.GetString()),
            JsonValueKind.Array => value.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString())
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            _ => null
        };
    }

    private static string? ReadIdentifierObject(JsonElement node)
    {
        if (!node.TryGetProperty("identifier", out var identifier) || identifier.ValueKind != JsonValueKind.Object) return null;
        return ReadString(identifier, "value") ?? ReadString(identifier, "name");
    }

    private static string? ReadHiringOrganization(JsonElement node)
    {
        if (!node.TryGetProperty("hiringOrganization", out var org)) return null;

        return org.ValueKind switch
        {
            JsonValueKind.String => JobTextUtilities.NullIfBlank(org.GetString()),
            JsonValueKind.Object => HtmlTextUtilities.CleanInline(ReadString(org, "name")),
            _ => null
        };
    }

    private static string? ReadLogo(JsonElement node)
    {
        if (node.TryGetProperty("hiringOrganization", out var org) && org.ValueKind == JsonValueKind.Object)
        {
            var logo = ReadString(org, "logo");
            if (!string.IsNullOrWhiteSpace(logo)) return logo;

            if (org.TryGetProperty("logo", out var logoObj) && logoObj.ValueKind == JsonValueKind.Object)
                return ReadString(logoObj, "url");
        }

        return null;
    }

    // jobLocation → address → addressLocality / addressRegion.
    private static string? ReadJobLocation(JsonElement node)
    {
        if (!node.TryGetProperty("jobLocation", out var location)) return null;

        var first = location.ValueKind == JsonValueKind.Array
            ? location.EnumerateArray().FirstOrDefault()
            : location;

        if (first.ValueKind != JsonValueKind.Object) return null;
        if (!first.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.Object) return null;

        var locality = ReadString(address, "addressLocality");
        var region = ReadString(address, "addressRegion");
        var parts = new[] { locality, region }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return parts.Length == 0 ? null : string.Join(", ", parts);
    }

    private static string? ReadJobCountry(JsonElement node)
    {
        if (!node.TryGetProperty("jobLocation", out var location)) return null;

        var first = location.ValueKind == JsonValueKind.Array
            ? location.EnumerateArray().FirstOrDefault()
            : location;

        if (first.ValueKind != JsonValueKind.Object) return null;
        if (!first.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.Object) return null;

        if (!address.TryGetProperty("addressCountry", out var country)) return null;

        return country.ValueKind switch
        {
            JsonValueKind.String => JobTextUtilities.NullIfBlank(country.GetString()),
            JsonValueKind.Object => ReadString(country, "name"),
            _ => null
        };
    }

    // baseSalary → value (MonetaryAmount → QuantitativeValue).
    private static string? ReadSalary(JsonElement node)
    {
        if (!node.TryGetProperty("baseSalary", out var salary)) return null;

        if (salary.ValueKind == JsonValueKind.String)
            return JobTextUtilities.NullIfBlank(salary.GetString());

        if (salary.ValueKind != JsonValueKind.Object) return null;

        var currency = ReadString(salary, "currency") ?? "ZAR";
        if (!salary.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) return null;

        var min = ReadNumberAsString(value, "minValue");
        var max = ReadNumberAsString(value, "maxValue");
        var flat = ReadNumberAsString(value, "value");
        var unit = ReadString(value, "unitText");

        var amount = (min, max, flat) switch
        {
            (not null, not null, _) => $"{min} - {max}",
            (not null, null, _) => min,
            (null, not null, _) => max,
            (null, null, not null) => flat,
            _ => null
        };

        if (amount is null) return null;

        var suffix = string.IsNullOrWhiteSpace(unit) ? string.Empty : $" per {unit.ToLowerInvariant()}";
        return $"{currency} {amount}{suffix}".Trim();
    }

    private static string? ReadNumberAsString(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => JobTextUtilities.NullIfBlank(value.GetString()),
            _ => null
        };
    }

    // ─── Feed readers ─────────────────────────────────────────────────

    private static string? ReadElement(XElement item, string name, XNamespace atom)
    {
        var element = item.Element(name) ?? item.Element(atom + name);
        return JobTextUtilities.NullIfBlank(element?.Value);
    }

    // Atom's <link href="…" /> carries the URL in an attribute.
    private static string? ReadAtomLinkHref(XElement item, XNamespace atom)
    {
        var link = item.Elements(atom + "link")
            .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate");
        return JobTextUtilities.NullIfBlank((string?)link?.Attribute("href"));
    }

    private static string? ReadContentEncoded(XElement item)
    {
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        return JobTextUtilities.NullIfBlank(item.Element(content + "encoded")?.Value);
    }
}

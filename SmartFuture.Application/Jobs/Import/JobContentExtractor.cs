using System.Text.Json;
using System.Xml.Linq;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Import;

// Turns a fetched page into candidate jobs.
//
// Order matters. A page is FIRST classified as an archive or a detail
// page, because the most damaging failure this module can have is
// importing a category page as a single job: the archive's title becomes
// the job title, the whole page (nav, sidebar, tag cloud, push-notify
// prompt) becomes the description, and a random link becomes the apply
// URL. An archive therefore yields child URLs and no jobs at all; the
// import service fetches those children and each comes back through here
// as its own detail page.
//
// For a detail page the strategies are, strongest-first:
//
//   1. JSON-LD schema.org/JobPosting — structured, authoritative, and
//      what every serious job board publishes for Google for Jobs.
//   2. RSS / Atom — used when the source is configured as a feed.
//   3. Article fallback — the post's own content region becomes ONE
//      opportunity. Splitting an article that lists several roles in
//      prose is where naive crawlers produce nonsense titles, so a
//      single correctly-attributed entry with a visible source link wins.
public class JobContentExtractor : IJobContentExtractor
{
    // Below this, the "article" isn't a job ad — it's a nav page or an
    // error/anti-bot interstitial. Applies when we had to read the whole
    // document, where site chrome inflates the count.
    private const int MinimumArticleLength = 400;

    // When the post's own content container was found, the text measured
    // is the ad and nothing else, so the bar has to be lower — plenty of
    // real SA listings are three short paragraphs and an email address.
    private const int MinimumContentLength = 200;

    public JobExtractionResult Extract(JobSource source, string content, string? contentType, DateTime nowUtc, string? pageUrl = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return JobExtractionResult.Empty("Source returned an empty body.");

        return source.SourceType switch
        {
            JobSourceType.RssFeed => ExtractFromFeed(source, content, nowUtc),
            JobSourceType.Api => JobExtractionResult.Empty(
                "Api source type has no extractor yet. Configure the source as RssFeed or HtmlPage, or capture its jobs manually."),
            JobSourceType.Manual => JobExtractionResult.Empty("Manual sources are never crawled."),
            _ => ExtractFromHtml(source, content, nowUtc, pageUrl ?? source.SourceUrl)
        };
    }

    public JobPageAnalysis Analyze(JobSource source, string content, string? contentType, string? pageUrl = null)
    {
        // Feeds and APIs are never archives — every item is already a
        // discrete listing.
        if (source.SourceType is JobSourceType.RssFeed or JobSourceType.Api or JobSourceType.Manual)
            return new JobPageAnalysis { Kind = JobPageKind.Detail };

        if (string.IsNullOrWhiteSpace(content) || LooksLikeXmlFeed(content))
            return new JobPageAnalysis { Kind = JobPageKind.Detail };

        return JobListingLinkExtractor.Analyze(content, pageUrl ?? source.SourceUrl, Math.Max(1, source.MaxJobsPerRun));
    }

    // ─── HTML ─────────────────────────────────────────────────────────

    private JobExtractionResult ExtractFromHtml(JobSource source, string html, DateTime nowUtc, string? pageUrl)
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

        // The guard. An archive reaching this method — because a caller
        // extracted without analysing first — must still not become a
        // job. It returns its child URLs so the mistake is recoverable.
        var analysis = JobListingLinkExtractor.Analyze(html, pageUrl, Math.Max(1, source.MaxJobsPerRun));
        if (analysis.IsListing)
        {
            result.StrategyUsed = "listing";
            result.Notes.AddRange(analysis.Notes);
            result.Notes.Add("No job was created from this page: it is an index of other posts, not a vacancy.");
            result.ChildUrls.AddRange(analysis.ChildUrls);
            result.NextPageUrl = analysis.NextPageUrl;
            return result;
        }

        var jsonLdJobs = ExtractJsonLdJobPostings(source, html, nowUtc, result.Notes, pageUrl);
        if (jsonLdJobs.Count > 0)
        {
            result.Jobs.AddRange(jsonLdJobs);
            result.StrategyUsed = "json-ld";
            result.Notes.Add($"Extracted {jsonLdJobs.Count} job(s) from schema.org JobPosting JSON-LD.");
            return result;
        }

        var article = ExtractArticle(source, html, nowUtc, result.Notes, pageUrl);
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

    private static List<ExtractedJob> ExtractJsonLdJobPostings(JobSource source, string html, DateTime nowUtc, List<string> notes, string? pageUrl)
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

                    var job = MapJsonLdJobPosting(element, source, nowUtc, pageUrl);
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

    private static ExtractedJob? MapJsonLdJobPosting(JsonElement node, JobSource source, DateTime nowUtc, string? pageUrl)
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
            SourceUrl = pageUrl ?? applyUrl ?? source.SourceUrl,
            ExternalId = ReadString(node, "identifier") ?? ReadIdentifierObject(node),
            PostedDateUtc = JobFieldParsers.ParseDate(ReadString(node, "datePosted")),
            ClosingDateUtc = JobFieldParsers.ParseDate(ReadString(node, "validThrough")),
            LogoUrl = ReadLogo(node),
            ExtractionStrategy = "json-ld"
        };

        job.WorkplaceType = ResolveWorkplaceType(node, job);
        job.Location = JobFieldParsers.BuildDisplayLocation(job.Location, job.City, job.Province);
        job.Summary = JobTextUtilities.BuildSummary(job.DescriptionText);
        job.ApplyEmail = JobFieldParsers.ExtractApplyEmail(job.DescriptionText);
        DropSelfReferencingApplyUrl(job);
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
            job.ApplyEmail = JobFieldParsers.ExtractApplyEmail(job.DescriptionText);
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

    private static ExtractedJob? ExtractArticle(JobSource source, string html, DateTime nowUtc, List<string> notes, string? pageUrl)
    {
        // The title still comes from the whole document — og:title and
        // <h1> are outside the content container.
        var title = HtmlTextUtilities.ExtractTitle(html);

        // Everything else is read from the post's own content region.
        // This is what keeps the sidebar's "recent posts", the tag
        // cloud, the push-notification prompt and the footer's contact
        // address out of the description, the salary and the apply email.
        var stripped = HtmlTextUtilities.StripBoilerplate(html);
        var mainContent = HtmlTextUtilities.ExtractMainContent(stripped);
        var content = mainContent ?? stripped;
        var bodyText = HtmlTextUtilities.ToPlainText(content);

        if (string.IsNullOrWhiteSpace(title))
        {
            notes.Add("Could not determine a title for the page; nothing imported.");
            return null;
        }

        // Drop the author/date/category byline WordPress prints above
        // the post before anything reads the text.
        bodyText = JobFieldParsers.StripLeadingPostMeta(bodyText, title) ?? bodyText;

        var minimumLength = mainContent is null ? MinimumArticleLength : MinimumContentLength;
        if (bodyText.Length < minimumLength)
        {
            notes.Add($"Page body was too short to be a job listing ({bodyText.Length} chars). It may be an anti-bot or error page.");
            return null;
        }

        var (loc, city, province) = JobFieldParsers.ParseLocation(
            source.DefaultLocation ?? FindLocationInText(bodyText));

        var applyEmail = JobFieldParsers.ExtractApplyEmail(bodyText);
        // Apply links are searched inside the content only — picking one
        // from an archive's nav is how listings ended up pointing at an
        // unrelated vacancy.
        var applyUrl = FindApplyLink(content, pageUrl ?? source.SourceUrl);

        var job = new ExtractedJob
        {
            Title = title,
            // Deliberately NOT og:site_name: on an aggregator that is
            // the board, not the employer, and it ended up on every
            // imported listing as the hiring company.
            CompanyName = JobFieldParsers.ExtractEmployer(bodyText),
            Location = JobFieldParsers.BuildDisplayLocation(loc, city, province),
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
            SourceUrl = pageUrl ?? source.SourceUrl,
            PostedDateUtc = JobFieldParsers.ExtractPostedDate(bodyText, nowUtc)
                ?? JobFieldParsers.ParseDate(HtmlTextUtilities.ExtractMetaContent(html, "article:published_time")),
            ClosingDateUtc = JobFieldParsers.ExtractClosingDate(bodyText, nowUtc),
            LogoUrl = HtmlTextUtilities.ExtractMetaContent(html, "og:image"),
            RawContentSnapshot = HtmlTextUtilities.Snapshot(bodyText),
            ExtractionStrategy = "article"
        };

        DropSelfReferencingApplyUrl(job);
        job.ApplicationInstructions = BuildApplicationInstructions(job);

        if (job.ApplyUrl is null && job.ApplyEmail is null)
            notes.Add("No apply link or email found; the listing points back to its original source URL.");

        return job;
    }

    // An "Apply" button that links back to the page it is on is not an
    // application target — it is an anchor. Storing it produced listings
    // whose ApplyUrl and SourceUrl were the same link shown twice.
    private static void DropSelfReferencingApplyUrl(ExtractedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ApplyUrl) || string.IsNullOrWhiteSpace(job.SourceUrl)) return;

        if (JobListingLinkExtractor.NormalizeForComparison(job.ApplyUrl)
            == JobListingLinkExtractor.NormalizeForComparison(job.SourceUrl))
        {
            job.ApplyUrl = null;
        }
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

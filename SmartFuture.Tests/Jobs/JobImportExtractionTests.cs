using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Tests.Jobs;

// Extraction + de-duplication behaviour. These are the two places where
// a crawler quietly goes wrong: it either produces twins of the same
// listing, or it invents fields the source never stated.
public class JobImportExtractionTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static JobSource HtmlSource(string url = "https://example.co.za/jobs") => new()
    {
        Id = Guid.NewGuid(),
        SourceName = "Example Board",
        SourceUrl = url,
        SourceType = JobSourceType.HtmlPage,
        IsActive = true,
        MaxJobsPerRun = 50
    };

    // ─── Fingerprint / de-duplication ─────────────────────────────────

    [Fact]
    public void Fingerprint_is_stable_across_tracking_parameters_and_trailing_slashes()
    {
        var a = JobTextUtilities.ComputeFingerprint("https://example.co.za/job/1", "Driver", "Acme", "Durban");
        var b = JobTextUtilities.ComputeFingerprint("https://example.co.za/job/1/?utm_source=facebook#apply", "Driver", "Acme", "Durban");

        b.Should().Be(a, "the same listing re-shared with tracking params must not import twice");
    }

    [Fact]
    public void Fingerprint_ignores_case_and_punctuation_drift_in_the_title()
    {
        var a = JobTextUtilities.ComputeFingerprint("https://example.co.za/job/1", "Senior Driver (Code 14)", "Acme", "Durban");
        var b = JobTextUtilities.ComputeFingerprint("https://example.co.za/job/1", "senior driver code 14", "acme", "durban");

        b.Should().Be(a);
    }

    [Fact]
    public void Fingerprint_separates_different_roles_published_on_the_same_url()
    {
        var driver = JobTextUtilities.ComputeFingerprint("https://example.co.za/article", "Driver", "Acme", "Durban");
        var packer = JobTextUtilities.ComputeFingerprint("https://example.co.za/article", "Packer", "Acme", "Durban");

        packer.Should().NotBe(driver, "one article can advertise several distinct roles");
    }

    [Fact]
    public void Slug_collisions_resolve_deterministically()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "driver-acme", "driver-acme-2" };

        var slug = JobTextUtilities.BuildUniqueSlug("Driver", "Acme", candidate => taken.Contains(candidate));

        slug.Should().Be("driver-acme-3");
    }

    // ─── JSON-LD ──────────────────────────────────────────────────────

    [Fact]
    public void JsonLd_job_posting_is_extracted_with_its_structured_fields()
    {
        var html = """
            <html><head>
            <script type="application/ld+json">
            {
              "@context": "https://schema.org",
              "@type": "JobPosting",
              "title": "Warehouse Supervisor",
              "description": "<p>Lead the night shift team.</p><p>Requirements:</p><ul><li>Matric</li></ul>",
              "datePosted": "2026-07-20",
              "validThrough": "2026-09-15",
              "employmentType": "FULL_TIME",
              "hiringOrganization": { "@type": "Organization", "name": "Acme Logistics" },
              "jobLocation": { "@type": "Place", "address": { "addressLocality": "Durban", "addressRegion": "KwaZulu-Natal", "addressCountry": "ZA" } },
              "url": "https://example.co.za/job/42"
            }
            </script>
            </head><body>listing</body></html>
            """;

        var result = new JobContentExtractor().Extract(HtmlSource(), html, "text/html", NowUtc);

        result.StrategyUsed.Should().Be("json-ld");
        result.Jobs.Should().HaveCount(1);

        var job = result.Jobs[0];
        job.Title.Should().Be("Warehouse Supervisor");
        job.CompanyName.Should().Be("Acme Logistics");
        job.City.Should().Be("Durban");
        job.Province.Should().Be("KwaZulu-Natal");
        job.EmploymentType.Should().Be("Full-time");
        job.ClosingDateUtc.Should().Be(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));
        job.PostedDateUtc.Should().Be(new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));
        job.ApplyUrl.Should().Be("https://example.co.za/job/42");
        job.DescriptionText.Should().Contain("Lead the night shift team");
    }

    [Fact]
    public void JsonLd_telecommute_flag_marks_the_role_remote()
    {
        var html = """
            <script type="application/ld+json">
            { "@type": "JobPosting", "title": "Support Agent", "jobLocationType": "TELECOMMUTE", "description": "Help customers." }
            </script>
            <html><body>x</body></html>
            """;

        var result = new JobContentExtractor().Extract(HtmlSource(), html, "text/html", NowUtc);

        result.Jobs.Should().ContainSingle();
        result.Jobs[0].WorkplaceType.Should().Be(JobWorkplaceType.Remote);
    }

    [Fact]
    public void JsonLd_graph_wrapper_is_traversed()
    {
        var html = """
            <script type="application/ld+json">
            { "@context":"https://schema.org", "@graph": [ { "@type":"WebSite" }, { "@type":"JobPosting", "title":"Cashier", "description":"Serve customers at the till." } ] }
            </script>
            """;

        var result = new JobContentExtractor().Extract(HtmlSource(), html, "text/html", NowUtc);

        result.Jobs.Should().ContainSingle();
        result.Jobs[0].Title.Should().Be("Cashier");
    }

    [Fact]
    public void Malformed_json_ld_is_noted_and_does_not_throw()
    {
        var html = """
            <script type="application/ld+json">{ this is not json </script>
            <html><head><title>Nothing here</title></head><body>short</body></html>
            """;

        var result = new JobContentExtractor().Extract(HtmlSource(), html, "text/html", NowUtc);

        result.Jobs.Should().BeEmpty();
        result.Notes.Should().Contain(n => n.Contains("malformed JSON-LD", StringComparison.OrdinalIgnoreCase));
    }

    // ─── RSS ──────────────────────────────────────────────────────────

    [Fact]
    public void Rss_items_become_jobs()
    {
        var source = HtmlSource();
        source.SourceType = JobSourceType.RssFeed;
        source.DefaultLocation = "Cape Town";
        source.DefaultCategory = "General Work";

        var xml = """
            <?xml version="1.0"?>
            <rss version="2.0"><channel>
              <item>
                <title>General Worker</title>
                <link>https://example.co.za/job/7</link>
                <description>General worker needed. Closing date: 30 August 2026. Send CV to hr@acme.co.za</description>
                <guid>job-7</guid>
              </item>
            </channel></rss>
            """;

        var result = new JobContentExtractor().Extract(source, xml, "application/rss+xml", NowUtc);

        result.StrategyUsed.Should().Be("rss");
        result.Jobs.Should().ContainSingle();

        var job = result.Jobs[0];
        job.Title.Should().Be("General Worker");
        job.ApplyUrl.Should().Be("https://example.co.za/job/7");
        job.City.Should().Be("Cape Town");
        job.Province.Should().Be("Western Cape");
        job.Category.Should().Be("General Work");
        job.ClosingDateUtc.Should().Be(new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc));
        job.ApplyEmail.Should().Be("hr@acme.co.za");
        job.ApplicationInstructions.Should().Contain("hr@acme.co.za",
            "an email-only advert must still tell the reader exactly how to apply");
    }

    [Fact]
    public void Invalid_feed_xml_is_reported_not_thrown()
    {
        var source = HtmlSource();
        source.SourceType = JobSourceType.RssFeed;

        var result = new JobContentExtractor().Extract(source, "<rss><channel><item>", "application/rss+xml", NowUtc);

        result.Jobs.Should().BeEmpty();
        result.Notes.Should().Contain(n => n.Contains("not valid XML", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Article fallback ─────────────────────────────────────────────

    [Fact]
    public void Text_heavy_article_is_imported_as_one_grouped_opportunity()
    {
        var body = string.Join("\n", Enumerable.Repeat("We are hiring for several positions at our Johannesburg branch this month.", 12));
        var html = $"""
            <html><head><title>Vacancies at Acme Johannesburg</title>
            <meta property="og:site_name" content="Workjob" />
            </head><body>
            <h1>Vacancies at Acme Johannesburg</h1>
            <p>{body}</p>
            <p>Requirements:</p>
            <p>Matric certificate</p>
            <p>Valid driver's licence</p>
            <p>How to apply:</p>
            <p>Send your CV to careers@acme.co.za. Closing date: 20 September 2026.</p>
            </body></html>
            """;

        var result = new JobContentExtractor().Extract(HtmlSource(), html, "text/html", NowUtc);

        result.StrategyUsed.Should().Be("article");
        result.Jobs.Should().ContainSingle("v1 imports a multi-role article intact rather than mis-splitting it");

        var job = result.Jobs[0];
        job.Title.Should().Be("Vacancies at Acme Johannesburg");
        job.City.Should().Be("Johannesburg");
        job.Province.Should().Be("Gauteng");
        job.ApplyEmail.Should().Be("careers@acme.co.za");
        job.ClosingDateUtc.Should().Be(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc));
        job.RequirementsText.Should().Contain("Matric");
        job.SourceUrl.Should().Be("https://example.co.za/jobs", "the original source URL must stay visible");
    }

    [Fact]
    public void Anti_bot_or_error_page_is_rejected_with_an_explanatory_note()
    {
        var html = "<html><head><title>Access denied</title></head><body><p>Please enable JavaScript.</p></body></html>";

        var result = new JobContentExtractor().Extract(HtmlSource(), html, "text/html", NowUtc);

        result.Jobs.Should().BeEmpty();
        result.Notes.Should().Contain(n => n.Contains("too short", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Api_source_type_reports_that_it_has_no_extractor_yet()
    {
        var source = HtmlSource();
        source.SourceType = JobSourceType.Api;

        var result = new JobContentExtractor().Extract(source, "{}", "application/json", NowUtc);

        result.Jobs.Should().BeEmpty();
        result.Notes.Should().Contain(n => n.Contains("no extractor yet", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Parser guard rails ───────────────────────────────────────────

    [Fact]
    public void Implausible_closing_dates_are_ignored_rather_than_guessed()
    {
        // A "closing date" a decade out is a mis-parse, and acting on it
        // would keep a dead job listed indefinitely.
        JobFieldParsers.ExtractClosingDate("Closing date: 01 January 2040", NowUtc).Should().BeNull();
        JobFieldParsers.ExtractClosingDate("Closing date: 01 January 2010", NowUtc).Should().BeNull();
        JobFieldParsers.ExtractClosingDate("Closing date: 20 September 2026", NowUtc)
            .Should().Be(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Two_letter_province_aliases_only_match_as_standalone_tokens()
    {
        // "Newcastle" contains "wc" — it must not become Western Cape.
        var (_, _, province) = JobFieldParsers.ParseLocation("Newcastle");
        province.Should().NotBe("Western Cape");

        var (_, _, explicitProvince) = JobFieldParsers.ParseLocation("Somerset West, WC");
        explicitProvince.Should().Be("Western Cape");
    }

    [Fact]
    public void Script_and_event_handlers_are_stripped_before_html_is_stored()
    {
        var dirty = "<div onclick=\"steal()\"><script>alert(1)</script><p>Real content</p><a href=\"javascript:evil()\">x</a></div>";

        var clean = HtmlTextUtilities.SanitizeHtml(dirty);

        clean.Should().NotBeNull();
        clean!.Should().NotContain("<script");
        clean.Should().NotContain("onclick");
        clean.Should().NotContain("javascript:");
        clean.Should().Contain("Real content");
    }
}

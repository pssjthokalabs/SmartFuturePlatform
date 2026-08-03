using System.Text;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Tests.Jobs;

// Guards the archive-vs-detail split.
//
// The defect these pin down: a WordPress category page was imported as a
// SINGLE job whose title was the archive's title, whose description was
// the entire rendered page (nav, sidebar, tag cloud, push-notification
// prompt, footer) and whose apply address came from the footer's contact
// block. Every test below is one of the ways that went wrong.
public class JobListingCrawlTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static JobSource Source(string url, int maxJobs = 20) => new()
    {
        Id = Guid.NewGuid(),
        SourceName = "Test Board",
        SourceUrl = url,
        SourceType = JobSourceType.HtmlPage,
        IsActive = true,
        MaxJobsPerRun = maxJobs,
        MaxPagesPerRun = 1
    };

    // A WordPress archive: repeated <article> cards, each with its post
    // link in an .entry-title heading, wrapped in the usual chrome.
    private static string WordPressArchive(IEnumerable<(string Slug, string Title)> posts, string? nextPageUrl = null,
        string host = "board.co.za")
    {
        var sb = new StringBuilder();
        sb.Append("""
            <html><head><title>Jobs Jobs in South Africa</title>
            <meta property="og:title" content="Jobs Jobs in South Africa" />
            </head><body>
            <header class="site-header"><nav class="main-navigation">
              <ul><li><a href="/">Home</a></li><li><a href="/category/jobs/">Jobs</a></li>
              <li><a href="/privacy-policy/">Privacy</a></li><li><a href="/disclaimer/">Disclaimer</a></li></ul>
            </nav></header>
            <main>
            """);

        foreach (var (slug, title) in posts)
        {
            sb.Append($"""
                <article class="post">
                  <header class="entry-header">
                    <h2 class="entry-title"><a href="https://{host}/{slug}/" rel="bookmark">{title}</a></h2>
                  </header>
                  <div class="entry-summary"><p>Applications are open.</p>
                    <a class="more-link" href="https://{host}/{slug}/">Read More</a></div>
                  <footer class="entry-footer"><a href="/category/jobs/">Jobs</a><a href="/tag/hiring/">hiring</a></footer>
                </article>
                """);
        }

        sb.Append("</main>");

        if (nextPageUrl is not null)
            sb.Append($"""<div class="pagination"><a class="next page-numbers" href="{nextPageUrl}">Next</a></div>""");

        sb.Append("""
            <aside class="sidebar">
              <div class="widget recent-posts"><h3>Recent Posts</h3><ul>
                <li><a href="https://{host}/sidebar-noise-one/">Sidebar Noise One</a></li>
                <li><a href="https://{host}/sidebar-noise-two/">Sidebar Noise Two</a></li></ul></div>
              <div class="widget tagcloud"><a href="/tag/driver/">driver</a><a href="/tag/admin/">admin</a></div>
              <div class="webpushr-prompt">Get notifications for new jobs!</div>
            </aside>
            <footer class="site-footer"><p>Contact us: info@board.co.za</p><p>Copyright 2026 Board</p></footer>
            </body></html>
            """);

        return sb.ToString();
    }

    // ─── 1. Archive produces child URLs, not one job ──────────────────

    [Fact]
    public void WordPress_archive_with_ten_posts_yields_ten_child_urls_and_no_job()
    {
        var posts = Enumerable.Range(1, 10).Select(i => ($"vacancy-{i}", $"Vacancy {i} Wanted")).ToList();
        var html = WordPressArchive(posts);
        var source = Source("https://board.co.za/category/jobs/");

        var analysis = new JobContentExtractor().Analyze(source, html, "text/html", source.SourceUrl);

        analysis.Kind.Should().Be(JobPageKind.Listing);
        analysis.ChildUrls.Should().HaveCount(10);
        analysis.ChildUrls.Should().OnlyContain(u => u.Contains("/vacancy-"));

        // And the extractor must refuse to make a job out of it even if
        // called directly.
        var extraction = new JobContentExtractor().Extract(source, html, "text/html", NowUtc, source.SourceUrl);
        extraction.Jobs.Should().BeEmpty("an archive page is never itself a vacancy");
        extraction.StrategyUsed.Should().Be("listing");
        extraction.ChildUrls.Should().HaveCount(10);
    }

    // ─── 2. MyCareers-style archive title is never imported ───────────

    [Fact]
    public void MyCareers_style_archive_does_not_import_its_own_title_as_a_job()
    {
        var html = WordPressArchive(new[]
        {
            ("general-worker-shoprite", "General Worker at Shoprite"),
            ("cashier-pnp", "Cashier at Pick n Pay"),
            ("driver-code-10", "Code 10 Driver Wanted"),
            ("security-officer", "Security Officer Vacancy"),
        }, host: "www.mycareers.co.za");

        var source = Source("https://www.mycareers.co.za/category/jobs/");
        var extraction = new JobContentExtractor().Extract(source, html, "text/html", NowUtc, source.SourceUrl);

        extraction.Jobs.Should().NotContain(j => j.Title!.Contains("Jobs Jobs in South Africa"));
        extraction.Jobs.Should().BeEmpty();
        extraction.ChildUrls.Should().HaveCount(4);
    }

    // ─── 3. WorkJob-style homepage splits into separate child URLs ────

    [Fact]
    public void WorkJob_style_homepage_yields_one_child_url_per_employer()
    {
        var html = WordPressArchive(new[]
        {
            ("dsv-vacancies", "DSV Vacancies 2026"),
            ("woolworths-jobs", "Woolworths Jobs"),
            ("unitrans-drivers", "Unitrans Driver Vacancies"),
            ("transnet-learnerships", "Transnet Learnerships"),
        }, host: "workjob.co.za");

        var source = Source("https://workjob.co.za/");
        var analysis = new JobContentExtractor().Analyze(source, html, "text/html", source.SourceUrl);

        analysis.Kind.Should().Be(JobPageKind.Listing);
        analysis.ChildUrls.Should().BeEquivalentTo(new[]
        {
            "https://workjob.co.za/dsv-vacancies/",
            "https://workjob.co.za/woolworths-jobs/",
            "https://workjob.co.za/unitrans-drivers/",
            "https://workjob.co.za/transnet-learnerships/",
        });
    }

    [Fact]
    public void Navigation_category_tag_and_pagination_links_are_never_treated_as_jobs()
    {
        var html = WordPressArchive(new[]
        {
            ("real-job-one", "Real Job One"),
            ("real-job-two", "Real Job Two"),
            ("real-job-three", "Real Job Three"),
        }, nextPageUrl: "https://board.co.za/category/jobs/page/2/");

        var analysis = new JobContentExtractor().Analyze(Source("https://board.co.za/category/jobs/"), html, "text/html",
            "https://board.co.za/category/jobs/");

        analysis.ChildUrls.Should().NotContain(u => u.Contains("/category/") || u.Contains("/tag/")
            || u.Contains("/privacy") || u.Contains("/disclaimer") || u.Contains("/page/"));
        analysis.ChildUrls.Should().HaveCount(3);
    }

    [Fact]
    public void Sidebar_recent_posts_are_not_mistaken_for_archive_cards()
    {
        // The sidebar carries two post links. They must not appear as
        // children, and they must not be what tips the page into being
        // classified as a listing.
        var html = WordPressArchive(new[]
        {
            ("real-job-one", "Real Job One"),
            ("real-job-two", "Real Job Two"),
            ("real-job-three", "Real Job Three"),
        });

        var analysis = new JobContentExtractor().Analyze(Source("https://board.co.za/"), html, "text/html", "https://board.co.za/");

        analysis.ChildUrls.Should().NotContain(u => u.Contains("sidebar-noise"));
    }

    // ─── 4. Boilerplate is stripped from detail pages ─────────────────

    private const string DetailPage = """
        <html><head><title>General Worker – Cape Town | Board</title>
        <meta property="og:title" content="General Worker – Cape Town" /></head><body>
        <header class="site-header"><nav class="main-navigation"><a href="/">Home</a><a href="/category/jobs/">Jobs</a></nav></header>
        <article class="post">
          <h1 class="entry-title">General Worker – Cape Town</h1>
          <div class="entry-content">
            <p>A logistics company in Cape Town is looking for general workers to join the warehouse team.
            The role involves picking, packing and loading stock for delivery across the Western Cape region.</p>
            <p>Requirements:</p>
            <ul><li>Matric certificate</li><li>Physically fit</li><li>Able to work shifts</li></ul>
            <p>Salary: R6 500 per month.</p>
            <p>To apply, send your CV to recruitment@logisticsco.co.za before the closing date.</p>
            <p><a class="apply-button" href="https://board.co.za/general-worker-cape-town/apply/">Apply Now</a></p>
          </div>
        </article>
        <aside class="sidebar">
          <div class="widget recent-posts"><h3>Recent Posts</h3>
            <a href="https://board.co.za/other-job/">Some Other Job</a></div>
          <div class="widget tagcloud"><a href="/tag/driver/">driver</a></div>
          <div class="webpushr-prompt">Allow notifications to get new jobs first! Click Allow.</div>
          <div class="widget newsletter">Subscribe to our newsletter</div>
        </aside>
        <footer class="site-footer">
          <p>Contact us on info@board.co.za or support@superio.com</p>
          <p>Copyright 2026 Board. All rights reserved. Theme by ThemeCo.</p>
        </footer>
        </body></html>
        """;

    [Fact]
    public void Detail_page_description_excludes_sidebar_footer_and_push_prompt()
    {
        var source = Source("https://board.co.za/general-worker-cape-town/");
        var result = new JobContentExtractor().Extract(source, DetailPage, "text/html", NowUtc, source.SourceUrl);

        result.Jobs.Should().ContainSingle();
        var description = result.Jobs[0].DescriptionText!;

        description.Should().Contain("picking, packing and loading");
        description.Should().NotContain("Recent Posts");
        description.Should().NotContain("Allow notifications");
        description.Should().NotContain("Subscribe to our newsletter");
        description.Should().NotContain("All rights reserved");
        description.Should().NotContain("Theme by ThemeCo");
    }

    // ─── 7. Detail parsing still produces a real job ──────────────────

    [Fact]
    public void Detail_page_still_parses_into_a_complete_job()
    {
        var source = Source("https://board.co.za/general-worker-cape-town/");
        var result = new JobContentExtractor().Extract(source, DetailPage, "text/html", NowUtc, source.SourceUrl);

        result.Jobs.Should().ContainSingle();
        var job = result.Jobs[0];

        job.Title.Should().Be("General Worker – Cape Town");
        job.City.Should().Be("Cape Town");
        job.Province.Should().Be("Western Cape");
        job.SalaryText.Should().Contain("6 500");
        job.RequirementsText.Should().Contain("Matric");
        job.SourceUrl.Should().Be("https://board.co.za/general-worker-cape-town/");
        job.ApplyUrl.Should().Be("https://board.co.za/general-worker-cape-town/apply/");
    }

    // ─── 5. Years are not salaries ────────────────────────────────────

    [Theory]
    [InlineData("Copyright 2026 Board. All rights reserved.")]
    [InlineData("DSV Vacancies 2025 apply now")]
    [InlineData("Posted in Year2025 by admin")]
    [InlineData("Reference number R2026 for this post")]
    [InlineData("There are R20 comments on this article")]
    public void Years_reference_numbers_and_counts_are_not_parsed_as_salary(string text)
        => JobFieldParsers.ExtractSalary(text).Should().BeNull();

    [Theory]
    [InlineData("Salary: R6 500 per month", "6 500")]
    [InlineData("The successful candidate will earn R25 000 - R30 000", "25 000")]
    [InlineData("Remuneration R12,500.00 per month", "12,500.00")]
    public void Genuine_salary_figures_are_still_parsed(string text, string expectedFragment)
        => JobFieldParsers.ExtractSalary(text).Should().Contain(expectedFragment);

    [Fact]
    public void Archive_page_copyright_year_does_not_become_the_salary()
    {
        var html = WordPressArchive(new[] { ("a-job", "A Job"), ("b-job", "B Job"), ("c-job", "C Job") });
        var source = Source("https://board.co.za/category/jobs/");

        var result = new JobContentExtractor().Extract(source, html, "text/html", NowUtc, source.SourceUrl);

        // No job at all is created, so there is no bogus salary to carry.
        result.Jobs.Should().BeEmpty();
    }

    // ─── 6. Footer / theme emails are not apply addresses ─────────────

    [Fact]
    public void Footer_contact_email_is_not_used_as_the_apply_address()
    {
        var source = Source("https://board.co.za/general-worker-cape-town/");
        var result = new JobContentExtractor().Extract(source, DetailPage, "text/html", NowUtc, source.SourceUrl);

        var job = result.Jobs.Single();
        job.ApplyEmail.Should().Be("recruitment@logisticsco.co.za");
        job.ApplyEmail.Should().NotBe("info@board.co.za");
        job.ApplyEmail.Should().NotBe("support@superio.com");
    }

    [Theory]
    [InlineData("Questions? support@superio.com")]
    [InlineData("Email hr@company.co.za for details")]
    [InlineData("Reach us at info@somesite.co.za")]
    public void Theme_placeholder_and_generic_addresses_are_rejected(string text)
        => JobFieldParsers.ExtractApplyEmail(text).Should().BeNull();

    [Fact]
    public void An_address_the_page_points_applications_at_is_accepted_even_if_generic()
    {
        JobFieldParsers.ExtractApplyEmail("Send your CV to info@realemployer.co.za")
            .Should().Be("info@realemployer.co.za");
    }

    // ─── 8. Caps ──────────────────────────────────────────────────────

    [Fact]
    public void MaxJobsPerRun_caps_how_many_child_links_are_queued()
    {
        var posts = Enumerable.Range(1, 40).Select(i => ($"vacancy-{i}", $"Vacancy {i}")).ToList();
        var html = WordPressArchive(posts);
        var source = Source("https://board.co.za/category/jobs/", maxJobs: 5);

        var analysis = new JobContentExtractor().Analyze(source, html, "text/html", source.SourceUrl);

        analysis.ChildUrls.Should().HaveCount(5, "the cap is the outbound request budget, not just a row limit");
    }

    [Fact]
    public void The_number_of_links_found_is_reported_separately_from_the_number_queued()
    {
        // The confusion this fixes: a page with 11 jobs and a cap of 5
        // used to report "5 found, 5 imported", so nothing in the UI
        // said the other 6 existed.
        var posts = Enumerable.Range(1, 11).Select(i => ($"vacancy-{i}", $"Vacancy {i}")).ToList();
        var html = WordPressArchive(posts);
        var source = Source("https://board.co.za/category/jobs/", maxJobs: 5);

        var analysis = new JobContentExtractor().Analyze(source, html, "text/html", source.SourceUrl);

        analysis.TotalChildLinksFound.Should().Be(11, "the page really does have 11 jobs");
        analysis.ChildUrls.Should().HaveCount(5, "only 5 may be fetched this run");
        analysis.WasTruncatedByJobLimit.Should().BeTrue();
        analysis.Notes.Should().Contain(n => n.Contains("11 job link(s)"));
    }

    [Fact]
    public void A_page_within_the_cap_is_not_reported_as_truncated()
    {
        var html = WordPressArchive(new[] { ("a-job", "A"), ("b-job", "B"), ("c-job", "C") });
        var analysis = new JobContentExtractor().Analyze(Source("https://board.co.za/", maxJobs: 20), html, "text/html", "https://board.co.za/");

        analysis.TotalChildLinksFound.Should().Be(3);
        analysis.ChildUrls.Should().HaveCount(3);
        analysis.WasTruncatedByJobLimit.Should().BeFalse();
    }

    [Fact]
    public void The_same_post_linked_twice_on_one_archive_is_queued_once()
    {
        // Each card links its post from both the title and "Read More".
        var html = WordPressArchive(new[] { ("only-job", "Only Job"), ("second-job", "Second"), ("third-job", "Third") });

        var analysis = new JobContentExtractor().Analyze(Source("https://board.co.za/"), html, "text/html", "https://board.co.za/");

        analysis.ChildUrls.Should().OnlyHaveUniqueItems();
        analysis.ChildUrls.Should().HaveCount(3);
    }

    [Fact]
    public void Next_page_link_is_reported_for_paginated_archives()
    {
        var html = WordPressArchive(
            new[] { ("a-job", "A"), ("b-job", "B"), ("c-job", "C") },
            nextPageUrl: "https://board.co.za/category/jobs/page/2/");

        var analysis = new JobContentExtractor().Analyze(Source("https://board.co.za/category/jobs/"), html, "text/html",
            "https://board.co.za/category/jobs/");

        analysis.NextPageUrl.Should().Be("https://board.co.za/category/jobs/page/2/");
    }

    // ─── Extraction quality (findings from the live probe) ────────────

    // Fix 1: the IT keyword list contained "it ", which matched the tail
    // of "submit ", "permit ", "audit ", "visit " — so every job on a
    // government board came back as Information Technology.
    [Theory]
    [InlineData("Please submit your application before the closing date.")]
    [InlineData("A valid Professional Driving Permit is required.")]
    [InlineData("Applicants must visit the nearest office.")]
    [InlineData("An internal audit will be conducted.")]
    public void Ordinary_words_containing_it_no_longer_infer_information_technology(string body)
        => JobFieldParsers.InferCategory("General Worker Vacancy", body).Should().NotBe("Information Technology");

    [Theory]
    [InlineData("Department of Health Cleaner Jobs 2026 (X20 Posts) – Apply Online", "Healthcare")]
    [InlineData("Driver/Messengers (X26 Permanent Posts) –Department of Education", "Logistics & Transport")]
    [InlineData("General Worker Vacancies 2026: Redline Gate, Guard and Fence Patroller X16 Posts", "General Work")]
    [InlineData("IEC Jobs 2026 | 616 Electoral Commission Vacancies – Apply Online", "Government & Public Sector")]
    [InlineData("Department of Correctional Services (DCS)- Community Member Vacancies", "Government & Public Sector")]
    public void Real_titles_from_the_live_probe_categorise_correctly(string title, string expected)
        => JobFieldParsers.InferCategory(title, "Please submit your application. A permit may be required.")
            .Should().Be(expected);

    [Fact]
    public void A_genuine_it_role_is_still_information_technology()
    {
        JobFieldParsers.InferCategory("IT Support Technician", null).Should().Be("Information Technology");
        JobFieldParsers.InferCategory("Software Developer", null).Should().Be("Information Technology");
    }

    [Fact]
    public void Title_wins_over_an_accidental_body_keyword()
        => JobFieldParsers.InferCategory("Cleaner Required", "The successful candidate will submit weekly IT reports.")
            .Should().Be("General Work");

    // Fix 2: og:site_name put the aggregator's own domain on every job.
    [Theory]
    [InlineData("Employer: Gauteng Department of Health\nPosition: Cleaner", "Gauteng Department of Health")]
    [InlineData("Department: Limpopo Department of Agriculture and Rural Development", "Limpopo Department of Agriculture and Rural Development")]
    [InlineData("The Department of Correctional Services is inviting applications.", "Department of Correctional Services")]
    public void Employer_is_read_from_the_ad_body(string body, string expected)
        => JobFieldParsers.ExtractEmployer(body).Should().Be(expected);

    [Fact]
    public void Employer_is_null_rather_than_guessed_when_the_ad_never_names_one()
        => JobFieldParsers.ExtractEmployer("A great opportunity awaits. Apply today before the closing date.")
            .Should().BeNull();

    [Fact]
    public void The_aggregator_site_name_is_never_used_as_the_company()
    {
        var source = Source("https://board.co.za/general-worker-cape-town/");
        var html = DetailPage.Replace("</head>", """<meta property="og:site_name" content="mycareers.co.za" /></head>""");

        var job = new JobContentExtractor().Extract(source, html, "text/html", NowUtc, source.SourceUrl).Jobs.Single();

        job.CompanyName.Should().NotBe("mycareers.co.za");
    }

    // Fix 3: Location held whole headlines.
    [Theory]
    [InlineData("Limpopo Department of Education is Hiring Driver/Messengers (X26 Permanent Posts)", "Limpopo")]
    [InlineData("Employer: Gauteng Department of Health", "Gauteng")]
    public void Display_location_falls_back_to_the_resolved_province(string raw, string expected)
    {
        var (location, city, province) = JobFieldParsers.ParseLocation(raw);
        JobFieldParsers.BuildDisplayLocation(location, city, province).Should().Be(expected);
    }

    [Fact]
    public void Display_location_prefers_city_and_province_together()
    {
        var (location, city, province) = JobFieldParsers.ParseLocation("Cape Town");
        JobFieldParsers.BuildDisplayLocation(location, city, province).Should().Be("Cape Town, Western Cape");
    }

    [Fact]
    public void An_unrecognised_but_short_place_name_is_kept()
        => JobFieldParsers.BuildDisplayLocation("Weskoppies Hospital", null, null).Should().Be("Weskoppies Hospital");

    [Fact]
    public void An_unrecognised_sentence_is_dropped_rather_than_shown_as_a_location()
        => JobFieldParsers.BuildDisplayLocation("The company is hiring across the country right now", null, null)
            .Should().BeNull();

    // Fix 4: an "Apply" anchor pointing at its own page.
    [Fact]
    public void An_apply_link_pointing_at_the_listing_itself_is_dropped()
    {
        const string selfReferencing = """
            <html><head><meta property="og:title" content="Cleaner Vacancy" /></head><body>
            <article><div class="entry-content">
              <p>The Gauteng Department of Health is looking for cleaners at Weskoppies Hospital.
              Duties include cleaning wards, offices and corridors, removing waste and restocking consumables.</p>
              <p>Requirements:</p><ul><li>Grade 10</li><li>Physically fit</li></ul>
              <p>Salary: R144 024 per annum.</p>
              <p><a href="https://board.co.za/cleaner-vacancy/">COMPLETE JOB APPLICATION</a></p>
            </div></article></body></html>
            """;

        var source = Source("https://board.co.za/cleaner-vacancy/");
        var job = new JobContentExtractor().Extract(source, selfReferencing, "text/html", NowUtc, source.SourceUrl).Jobs.Single();

        job.SourceUrl.Should().Be("https://board.co.za/cleaner-vacancy/");
        job.ApplyUrl.Should().BeNull("an anchor back to the same page is not an application target");
    }

    // Minor: WordPress byline residue.
    [Fact]
    public void Wordpress_byline_is_stripped_from_the_top_of_the_description()
    {
        var text = JobFieldParsers.StripLeadingPostMeta(
            "Cleaner Jobs 2026\nadmin\nJuly 15, 2026\nJobs\n0 Comments\n\nThe Gauteng Department of Health has opened applications.",
            "Cleaner Jobs 2026");

        text.Should().StartWith("The Gauteng Department of Health");
        text.Should().NotContain("admin");
        text.Should().NotContain("July 15, 2026");
    }

    [Fact]
    public void A_long_repeated_headline_is_still_stripped()
    {
        // Regression: the length guard used to run before the title
        // check, so a real 68-character post title stopped the strip on
        // its very first line and nothing was removed at all.
        const string title = "Department of Correctional Services (DCS)- Community Member Vacancies";
        var text = JobFieldParsers.StripLeadingPostMeta($"{title}\n\nadmin\n\nJuly 16, 2026\n\nJobs\n\nApplications are invited.", title);

        text.Should().StartWith("Applications are invited");
        text.Should().NotContain("admin");
    }

    [Fact]
    public void Stripping_stops_at_the_first_line_of_real_content()
    {
        var text = JobFieldParsers.StripLeadingPostMeta("admin\nApplications are open for cleaners.\nJuly 15, 2026", null);

        text.Should().StartWith("Applications are open");
        text.Should().Contain("July 15, 2026", "a date inside the body is content, not a byline");
    }

    // ─── Boilerplate stripping must not eat the page ──────────────────

    // WorkJob's theme advertises its layout on <body>:
    //   class="… has-site-branding has-right-sidebar"
    // "has-right-sidebar" contains "sidebar", so the whole <body> was
    // removed, 98% of the document vanished, and a homepage with eleven
    // job cards extracted zero links.
    private const string WorkJobShapedArchive = """
        <html><head><title>WorkJob</title></head>
        <body class="home blog wp-custom-logo wp-theme-bezel hfeed has-site-branding has-right-sidebar">
        <header id="masthead" class="site-header"><nav class="main-navigation">
          <a href="/category/general-workers/">GENERAL WORKERS</a><a href="/privacy-policy/">PRIVACY POLICY</a>
        </nav></header>
        <main id="main" class="site-main">
          <article class="post type-post hentry">
            <h1 class="entry-title"><a href="https://workjob.co.za/2026/07/28/dsv-material-handler-warehouse-worker/" rel="bookmark">DSV Material Handler</a></h1>
            <a class="more-link" href="https://workjob.co.za/2026/07/28/dsv-material-handler-warehouse-worker/">Read More</a>
          </article>
          <article class="post type-post hentry">
            <h1 class="entry-title"><a href="https://workjob.co.za/2026/08/02/unitrans-general-worker/" rel="bookmark">Unitrans General Worker</a></h1>
            <a class="more-link" href="https://workjob.co.za/2026/08/02/unitrans-general-worker/">Read More</a>
          </article>
          <article class="post type-post hentry">
            <h1 class="entry-title"><a href="https://workjob.co.za/2026/07/30/legit-shop-assistant-cashier-jobs/" rel="bookmark">Legit Shop Assistant</a></h1>
            <a class="more-link" href="https://workjob.co.za/2026/07/30/legit-shop-assistant-cashier-jobs/">Read More</a>
          </article>
          <article class="post type-post hentry">
            <h1 class="entry-title"><a href="https://workjob.co.za/2026/07/27/ram-hand-to-hand-couriers-jobs/" rel="bookmark">RAM Couriers</a></h1>
          </article>
        </main>
        <div id="site-sidebar" class="sidebar-area col-lg-4">
          <aside class="widget"><a href="https://workjob.co.za/2020/01/01/sidebar-noise-post/">Old sidebar post</a></aside>
        </div>
        <nav class="navigation pagination"><a class="next page-numbers" href="https://workjob.co.za/page/2/">Next</a></nav>
        <footer id="colophon" class="site-footer">info@workjob.co.za</footer>
        </body></html>
        """;

    [Fact]
    public void A_body_class_containing_sidebar_does_not_delete_the_whole_page()
    {
        var stripped = HtmlTextUtilities.StripBoilerplate(WorkJobShapedArchive);

        stripped.Should().Contain("dsv-material-handler", "the <body> must survive its own layout class");
        stripped.Length.Should().BeGreaterThan(WorkJobShapedArchive.Length / 2);
    }

    [Fact]
    public void Stripping_never_removes_html_body_or_main_whatever_their_class_says()
    {
        var removed = HtmlTextUtilities.DescribeBoilerplateRemovals(WorkJobShapedArchive);

        removed.Should().NotContain(r => r.StartsWith("<body>", StringComparison.Ordinal));
        removed.Should().NotContain(r => r.StartsWith("<html>", StringComparison.Ordinal));
        removed.Should().NotContain(r => r.StartsWith("<main>", StringComparison.Ordinal));
    }

    [Fact]
    public void Stripping_still_removes_the_sidebar_and_footer()
    {
        var stripped = HtmlTextUtilities.StripBoilerplate(WorkJobShapedArchive);

        stripped.Should().NotContain("sidebar-noise-post");
        stripped.Should().NotContain("info@workjob.co.za");
    }

    [Fact]
    public void A_workjob_shaped_archive_yields_one_child_url_per_card()
    {
        var source = Source("https://workjob.co.za/");
        var analysis = new JobContentExtractor().Analyze(source, WorkJobShapedArchive, "text/html", "https://workjob.co.za/");

        analysis.Kind.Should().Be(JobPageKind.Listing);
        analysis.ChildUrls.Should().HaveCount(4);
        analysis.ChildUrls.Should().NotContain(u => u.Contains("sidebar-noise"));
        analysis.NextPageUrl.Should().Be("https://workjob.co.za/page/2/");
    }

    [Fact]
    public void The_workjob_archive_creates_no_job_of_its_own()
    {
        var source = Source("https://workjob.co.za/");
        var result = new JobContentExtractor().Extract(source, WorkJobShapedArchive, "text/html", NowUtc, "https://workjob.co.za/");

        result.Jobs.Should().BeEmpty();
        result.StrategyUsed.Should().Be("listing");
        result.ChildUrls.Should().HaveCount(4);
    }

    [Theory]
    [InlineData("https://workjob.co.za/2026/07/28/dsv-material-handler-warehouse-worker/", true)]
    [InlineData("https://workjob.co.za/some-multi-word-job-slug/", true)]
    [InlineData("https://workjob.co.za/about/", false)]
    [InlineData("https://workjob.co.za/", false)]
    public void Article_shaped_urls_are_recognised(string url, bool expected)
        => JobListingLinkExtractor.LooksLikeArticleUrl(url).Should().Be(expected);

    [Fact]
    public void Link_extraction_falls_back_to_raw_markup_when_stripping_over_matches()
    {
        // A theme that wraps its post loop in a widget-ish class, where
        // the wrapper is small enough that the size guard allows its
        // removal. The stripped pass then finds nothing, and the raw
        // pass is what saves the page.
        var filler = string.Concat(Enumerable.Repeat(
            "<p>Editorial copy about applying for jobs in South Africa, repeated to make the page body substantial.</p>", 12));

        var html = $$"""
            <html><body>
            <div class="intro">{{filler}}</div>
            <div class="widget-loop">
              <article><h2><a href="https://board.co.za/first-job-here/">First Job Here</a></h2></article>
              <article><h2><a href="https://board.co.za/second-job-here/">Second Job Here</a></h2></article>
              <article><h2><a href="https://board.co.za/third-job-here/">Third Job Here</a></h2></article>
            </div></body></html>
            """;

        // Precondition: the wrapper really is removed by stripping.
        HtmlTextUtilities.StripBoilerplate(html).Should().NotContain("first-job-here");

        var analysis = new JobContentExtractor().Analyze(Source("https://board.co.za/"), html, "text/html", "https://board.co.za/");

        analysis.Kind.Should().Be(JobPageKind.Listing);
        analysis.ChildUrls.Should().HaveCount(3);
        analysis.Notes.Should().Contain(n => n.Contains("raw page"));
    }

    [Fact]
    public void A_security_company_listing_is_categorised_as_security()
        => JobFieldParsers.InferCategory("24/7 Security Services Careers", null).Should().Be("Security");

    // ─── Import status ────────────────────────────────────────────────

    [Fact]
    public void A_new_source_does_not_auto_publish_what_it_imports()
    {
        // Crawled content reaches the public site only after a human
        // looks at it, unless this source is explicitly trusted.
        new JobSource().AutoPublish.Should().BeFalse();
    }

    [Fact]
    public void A_detail_page_with_a_couple_of_related_links_is_still_a_detail_page()
    {
        var source = Source("https://board.co.za/general-worker-cape-town/");
        var analysis = new JobContentExtractor().Analyze(source, DetailPage, "text/html", source.SourceUrl);

        analysis.Kind.Should().Be(JobPageKind.Detail);
        analysis.ChildUrls.Should().BeEmpty();
    }
}

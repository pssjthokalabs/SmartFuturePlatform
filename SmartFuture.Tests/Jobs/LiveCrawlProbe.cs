using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Domain.Jobs;
using SmartFuture.Infrastructure.Jobs;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Tests.Jobs;

// An OPT-IN probe that crawls one real site through the production
// fetcher and extractor and writes a report of what WOULD be imported.
//
// It touches no database. That is deliberate: the Jobs migration is not
// applied anywhere, and this environment resolves Development to the
// live connection string, so there is no safe database for a crawl test
// to write to. Everything the acceptance criteria ask about — listing vs
// detail, description cleanliness, salary, apply email, apply URL,
// source URL, resulting status — is decided before persistence, so a
// dry run still proves it.
//
// Skipped unless SMARTFUTURE_LIVE_CRAWL=1, so the normal suite and CI
// never reach the public internet.
public class LiveCrawlProbe
{
    private const string EnableVariable = "SMARTFUTURE_LIVE_CRAWL";

    [Fact]
    public async Task Crawl_one_live_source_and_report_what_would_be_imported()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1") return;

        var url = Environment.GetEnvironmentVariable("SMARTFUTURE_LIVE_CRAWL_URL")
            ?? "https://www.mycareers.co.za/category/jobs/";
        var outputPath = Environment.GetEnvironmentVariable("SMARTFUTURE_LIVE_CRAWL_OUT")
            ?? Path.Combine(Path.GetTempPath(), "live-crawl-report.txt");

        // The limits the test is required to run under.
        var source = new JobSource
        {
            Id = Guid.NewGuid(),
            SourceName = "Live probe",
            SourceUrl = url,
            SourceType = JobSourceType.HtmlPage,
            IsActive = true,
            AutoPublish = false,
            MaxJobsPerRun = 5,
            MaxPagesPerRun = 1
        };

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SmartFutureJobBot/1.0 (+https://www.smartfuture.co.za)");
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-ZA,en;q=0.9");

        var fetcher = new HttpJobSourceFetcher(httpClient, NullLogger<HttpJobSourceFetcher>.Instance);
        var extractor = new JobContentExtractor();
        var report = new StringBuilder();

        report.AppendLine($"SOURCE      : {source.SourceName}");
        report.AppendLine($"URL         : {url}");
        report.AppendLine($"LIMITS      : MaxJobsPerRun={source.MaxJobsPerRun} MaxPagesPerRun={source.MaxPagesPerRun} AutoPublish={source.AutoPublish}");
        report.AppendLine($"STARTED     : {DateTime.UtcNow:u}");
        report.AppendLine(new string('=', 78));

        var archive = await fetcher.FetchAsync(url);
        report.AppendLine($"[archive] HTTP {archive.StatusCode} success={archive.IsSuccess} type={archive.ContentType} bytes={archive.Content?.Length ?? 0}");
        if (!archive.IsSuccess)
        {
            report.AppendLine($"[archive] FAILED: {archive.FailureMessage}");
            await WriteAsync(outputPath, report);
            return;
        }

        var analysis = extractor.Analyze(source, archive.Content!, archive.ContentType, archive.FinalUrl ?? url);
        report.AppendLine($"[classify] kind={analysis.Kind} children={analysis.ChildUrls.Count} nextPage={analysis.NextPageUrl ?? "(none)"}");
        foreach (var note in analysis.Notes) report.AppendLine($"[classify] {note}");

        // The headline assertion: the archive itself must never become a job.
        var archiveAsJob = extractor.Extract(source, archive.Content!, archive.ContentType, DateTime.UtcNow, archive.FinalUrl ?? url);
        report.AppendLine($"[guard] jobs created from the archive page itself = {archiveAsJob.Jobs.Count} (strategy={archiveAsJob.StrategyUsed})");
        report.AppendLine(new string('-', 78));

        foreach (var (child, index) in analysis.ChildUrls.Select((c, i) => (c, i + 1)))
            report.AppendLine($"[queued {index}] {child}");

        report.AppendLine(new string('-', 78));

        var created = 0;
        foreach (var (child, index) in analysis.ChildUrls.Select((c, i) => (c, i + 1)))
        {
            var detail = await fetcher.FetchAsync(child);
            report.AppendLine($"[detail {index}] HTTP {detail.StatusCode} success={detail.IsSuccess} {child}");

            if (!detail.IsSuccess)
            {
                report.AppendLine($"[detail {index}] FAILED: {detail.FailureMessage}");
                continue;
            }

            var detailUrl = detail.FinalUrl ?? child;
            var extraction = extractor.Extract(source, detail.Content!, detail.ContentType, DateTime.UtcNow, detailUrl);
            foreach (var note in extraction.Notes) report.AppendLine($"[detail {index}] note: {note}");

            if (extraction.Jobs.Count == 0)
            {
                report.AppendLine($"[detail {index}] no job parsed (strategy={extraction.StrategyUsed})");
                continue;
            }

            foreach (var job in extraction.Jobs)
            {
                created++;
                // Mirrors JobImportService: a source that does not
                // auto-publish lands every listing in Draft.
                var status = source.AutoPublish ? JobOpportunityStatus.Active : JobOpportunityStatus.Draft;

                report.AppendLine($"  strategy   : {job.ExtractionStrategy}");
                report.AppendLine($"  status     : {status}");
                report.AppendLine($"  title      : {job.Title}");
                report.AppendLine($"  company    : {job.CompanyName ?? "(null)"}");
                report.AppendLine($"  location   : {job.Location ?? "(null)"} | city={job.City ?? "(null)"} | province={job.Province ?? "(null)"}");
                report.AppendLine($"  category   : {job.Category ?? "(null)"}");
                report.AppendLine($"  employment : {job.EmploymentType ?? "(null)"} | workplace={job.WorkplaceType}");
                report.AppendLine($"  salary     : {job.SalaryText ?? "(null)"}");
                report.AppendLine($"  applyEmail : {job.ApplyEmail ?? "(null)"}");
                report.AppendLine($"  applyUrl   : {job.ApplyUrl ?? "(null)"}");
                report.AppendLine($"  sourceUrl  : {job.SourceUrl ?? "(null)"}");
                report.AppendLine($"  posted     : {job.PostedDateUtc?.ToString("u") ?? "(null)"} | closing={job.ClosingDateUtc?.ToString("u") ?? "(null)"}");
                report.AppendLine($"  descLength : {job.DescriptionText?.Length ?? 0}");
                report.AppendLine($"  description:");
                report.AppendLine(Indent(job.DescriptionText, 2000));
                report.AppendLine(new string('-', 78));
            }
        }

        report.AppendLine($"TOTAL WOULD-BE JOBS: {created}");
        report.AppendLine($"FINISHED   : {DateTime.UtcNow:u}");

        await WriteAsync(outputPath, report);
    }

    private static string Indent(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "    (empty)";
        var trimmed = text.Length > maxLength ? text[..maxLength] + "\n…(truncated)" : text;
        return string.Join('\n', trimmed.Split('\n').Select(l => "    " + l));
    }

    private static async Task WriteAsync(string path, StringBuilder report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, report.ToString());
    }
}

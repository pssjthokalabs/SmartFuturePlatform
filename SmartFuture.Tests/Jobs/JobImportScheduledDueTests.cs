using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Jobs;

// Brief §19/Priority 10: "a source becomes due automatically without
// admin pressing Refresh." JobImportHostedService's scheduled loop
// calls RunAllAsync(Scheduled, onlyDue: true) every 15 minutes — these
// tests pin the "due" selection logic that loop depends on, using the
// exact same JobImportService the hosted service resolves from DI.
public class JobImportScheduledDueTests
{
    private static JobSource NewSource(
        string name, JobSourceType type, bool isActive, int? crawlFrequencyMinutes, DateTime? lastCheckedAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        SourceName = name,
        SourceUrl = $"https://board.co.za/{Guid.NewGuid():N}/jobs/",
        SourceType = type,
        IsActive = isActive,
        CrawlFrequencyMinutes = crawlFrequencyMinutes,
        LastCheckedAtUtc = lastCheckedAtUtc,
        MaxJobsPerRun = 1,
        MaxPagesPerRun = 1
    };

    private static JobImportService BuildService(SqliteTestDbFixture fixture)
    {
        var jobService = new Mock<IJobOpportunityService>();
        jobService.Setup(s => s.ExpireClosedJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Success(0));

        // A fetcher that returns no content is enough — these tests only
        // care about WHICH sources get attempted by RunAllAsync(onlyDue:
        // true), not what a real crawl would find.
        return new JobImportService(
            fixture.AppDbContext,
            Mock.Of<IJobSourceFetcher>(),
            new JobContentExtractor(),
            jobService.Object,
            new JobImportQueue(),
            Mock.Of<IAuditService>(),
            Mock.Of<ICurrentUserService>(),
            NullLogger<JobImportService>.Instance);
    }

    [Fact]
    public async Task RunAllAsync_OnlyDue_IncludesSource_NeverCheckedButScheduled()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource("Never checked, daily", JobSourceType.HtmlPage, isActive: true,
            crawlFrequencyMinutes: 1440, lastCheckedAtUtc: null);
        fixture.DbContext.JobSources.Add(source);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var result = await service.RunAllAsync(JobImportRunTrigger.Scheduled, onlyDue: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Data!.SourcesAttempted);
    }

    [Fact]
    public async Task RunAllAsync_OnlyDue_IncludesSource_PastItsDailyWindow()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource("Overdue daily", JobSourceType.HtmlPage, isActive: true,
            crawlFrequencyMinutes: 1440, lastCheckedAtUtc: DateTime.UtcNow.AddHours(-25));
        fixture.DbContext.JobSources.Add(source);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var result = await service.RunAllAsync(JobImportRunTrigger.Scheduled, onlyDue: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Data!.SourcesAttempted);
    }

    [Fact]
    public async Task RunAllAsync_OnlyDue_ExcludesSource_CheckedRecently()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource("Checked an hour ago, daily", JobSourceType.HtmlPage, isActive: true,
            crawlFrequencyMinutes: 1440, lastCheckedAtUtc: DateTime.UtcNow.AddHours(-1));
        fixture.DbContext.JobSources.Add(source);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var result = await service.RunAllAsync(JobImportRunTrigger.Scheduled, onlyDue: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Data!.SourcesAttempted);
    }

    [Fact]
    public async Task RunAllAsync_OnlyDue_ExcludesManualOnlySource_RegardlessOfLastChecked()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        // No CrawlFrequencyMinutes at all — manual-refresh-only, per
        // JobSource.CrawlFrequencyMinutes' own doc comment.
        var source = NewSource("Manual only", JobSourceType.HtmlPage, isActive: true,
            crawlFrequencyMinutes: null, lastCheckedAtUtc: DateTime.UtcNow.AddYears(-1));
        fixture.DbContext.JobSources.Add(source);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var result = await service.RunAllAsync(JobImportRunTrigger.Scheduled, onlyDue: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Data!.SourcesAttempted);
    }

    [Fact]
    public async Task RunAllAsync_OnlyDue_ExcludesInactiveSource_EvenIfOverdue()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource("Disabled but overdue", JobSourceType.HtmlPage, isActive: false,
            crawlFrequencyMinutes: 1440, lastCheckedAtUtc: DateTime.UtcNow.AddDays(-5));
        fixture.DbContext.JobSources.Add(source);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var result = await service.RunAllAsync(JobImportRunTrigger.Scheduled, onlyDue: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Data!.SourcesAttempted);
    }

    [Fact]
    public async Task RunAllAsync_OnlyDue_OneBlockedSource_DoesNotStopOthers()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var blocked = NewSource("Blocked source", JobSourceType.HtmlPage, isActive: true,
            crawlFrequencyMinutes: 1440, lastCheckedAtUtc: null);
        var healthy = NewSource("Healthy source", JobSourceType.HtmlPage, isActive: true,
            crawlFrequencyMinutes: 1440, lastCheckedAtUtc: null);
        fixture.DbContext.JobSources.AddRange(blocked, healthy);
        await fixture.DbContext.SaveChangesAsync();

        var jobService = new Mock<IJobOpportunityService>();
        jobService.Setup(s => s.ExpireClosedJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Success(0));

        // Every fetch reports a clean failure (the documented
        // IJobSourceFetcher contract: "Never throws to the caller — a
        // blocked or unreachable source is DATA") — proves RunAllAsync
        // keeps attempting every due source rather than stopping after
        // the first one fails.
        var fetcher = new Mock<IJobSourceFetcher>();
        fetcher.Setup(f => f.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JobFetchResult.Failure("simulated network failure"));

        var service = new JobImportService(
            fixture.AppDbContext, fetcher.Object, new JobContentExtractor(), jobService.Object,
            new JobImportQueue(), Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<JobImportService>.Instance);

        var result = await service.RunAllAsync(JobImportRunTrigger.Scheduled, onlyDue: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data!.SourcesAttempted);
        Assert.Equal(0, result.Data.SourcesSucceeded);
        Assert.Equal(2, result.Data.SourcesFailed);
    }
}

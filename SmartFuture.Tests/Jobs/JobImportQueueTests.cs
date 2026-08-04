using Microsoft.EntityFrameworkCore;
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

namespace SmartFuture.Tests.Jobs;

// Manual refresh is asynchronous: the endpoint records a run and queues
// it, the hosted service crawls.
//
// THE DEFECT THIS REPLACES
//
// The crawl used to run inside the HTTP request. It fetches one page per
// MaxPagesPerRun plus one page per job, sequentially, each fetch up to
// 20s — minutes of work for a large source. Held in a request, it could
// be killed by Cloudflare, IIS/ANCM, the browser, an app-pool recycle,
// or a slow board, and each one surfaced as a different phantom bug (the
// Cloudflare cut wrote no response, so the browser blamed CORS). A
// bigger timeout only changes which layer does the killing.
//
// These tests pin the properties that make the async path safe: the
// request returns without crawling, one source cannot be crawled twice
// at once, and a run orphaned by a restart cannot block that source
// forever.
public class JobImportQueueTests
{
    // SourceUrl carries a unique index, so every source in a test needs
    // its own URL — two sources sharing one would fail on save rather
    // than on the behaviour under test.
    private static JobSource NewSource(string name = "Test Board", JobSourceType type = JobSourceType.HtmlPage) => new()
    {
        Id = Guid.NewGuid(),
        SourceName = name,
        SourceUrl = $"https://board.co.za/{Guid.NewGuid():N}/jobs/",
        SourceType = type,
        IsActive = true,
        MaxJobsPerRun = 3,
        MaxPagesPerRun = 1
    };

    private static JobImportService BuildService(SqliteTestDbFixture fixture, IJobImportQueue queue, IJobSourceFetcher? fetcher = null)
    {
        var jobService = new Mock<IJobOpportunityService>();
        jobService.Setup(s => s.ExpireClosedJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Success(0));

        return new JobImportService(
            fixture.AppDbContext,
            fetcher ?? Mock.Of<IJobSourceFetcher>(),
            new JobContentExtractor(),
            jobService.Object,
            queue,
            Mock.Of<IAuditService>(),
            Mock.Of<ICurrentUserService>(),
            NullLogger<JobImportService>.Instance);
    }

    // ─── The request no longer crawls ─────────────────────────────────

    [Fact]
    public async Task QueueSourceRefresh_returns_a_running_run_without_fetching_anything()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource();
        fixture.AppDbContext.JobSources.Add(source);
        await fixture.AppDbContext.SaveChangesAsync();

        // A fetcher that would fail the test if the request touched it:
        // the whole point is that no crawling happens inside the call.
        var fetcher = new Mock<IJobSourceFetcher>(MockBehavior.Strict);
        var queue = new JobImportQueue();
        var service = BuildService(fixture, queue, fetcher.Object);

        var result = await service.QueueSourceRefreshAsync(source.Id, JobImportRunTrigger.Manual);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Status.Should().Be(JobImportRunStatus.Running);
        result.Data.Id.Should().NotBeEmpty("the admin needs a runId to poll");
        result.Message.Should().Contain("Import started");

        queue.PendingCount.Should().Be(1, "the crawl must be handed to the worker, not run here");
        fetcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task QueueSourceRefresh_persists_the_run_so_polling_can_find_it_immediately()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource();
        fixture.AppDbContext.JobSources.Add(source);
        await fixture.AppDbContext.SaveChangesAsync();

        var service = BuildService(fixture, new JobImportQueue());
        var queued = await service.QueueSourceRefreshAsync(source.Id, JobImportRunTrigger.Manual);

        var fetched = await service.GetRunAsync(queued.Data!.Id);

        fetched.IsSuccess.Should().BeTrue();
        fetched.Data!.Status.Should().Be(JobImportRunStatus.Running);
        fetched.Data.SourceId.Should().Be(source.Id);
    }

    [Fact]
    public async Task QueueSourceRefresh_rejects_a_missing_source()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var service = BuildService(fixture, new JobImportQueue());

        var result = await service.QueueSourceRefreshAsync(Guid.NewGuid(), JobImportRunTrigger.Manual);

        result.IsSuccess.Should().BeFalse();
        result.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task QueueSourceRefresh_rejects_a_manual_source()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource(type: JobSourceType.Manual);
        fixture.AppDbContext.JobSources.Add(source);
        await fixture.AppDbContext.SaveChangesAsync();

        var queue = new JobImportQueue();
        var result = await BuildService(fixture, queue).QueueSourceRefreshAsync(source.Id, JobImportRunTrigger.Manual);

        result.IsSuccess.Should().BeFalse();
        queue.PendingCount.Should().Be(0, "a manual source must never be queued for crawling");
    }

    // ─── Duplicate protection ─────────────────────────────────────────

    [Fact]
    public async Task A_second_refresh_returns_the_run_already_in_flight()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource();
        fixture.AppDbContext.JobSources.Add(source);
        await fixture.AppDbContext.SaveChangesAsync();

        var queue = new JobImportQueue();
        var service = BuildService(fixture, queue);

        var first = await service.QueueSourceRefreshAsync(source.Id, JobImportRunTrigger.Manual);
        var second = await service.QueueSourceRefreshAsync(source.Id, JobImportRunTrigger.Manual);

        second.IsSuccess.Should().BeTrue("a double click is not an error");
        second.Data!.Id.Should().Be(first.Data!.Id, "the admin should be tracking the same run");
        second.Message.Should().Contain("already running");

        queue.PendingCount.Should().Be(1, "two crawlers must never race over the same source");
        fixture.AppDbContext.JobImportRuns.Count().Should().Be(1, "history must not fill with phantom runs");
    }

    [Fact]
    public async Task A_different_source_is_not_blocked_by_another_sources_run()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var first = NewSource("Board A");
        var second = NewSource("Board B");
        fixture.AppDbContext.JobSources.AddRange(first, second);
        await fixture.AppDbContext.SaveChangesAsync();

        var queue = new JobImportQueue();
        var service = BuildService(fixture, queue);

        await service.QueueSourceRefreshAsync(first.Id, JobImportRunTrigger.Manual);
        var result = await service.QueueSourceRefreshAsync(second.Id, JobImportRunTrigger.Manual);

        result.IsSuccess.Should().BeTrue();
        queue.PendingCount.Should().Be(2);
    }

    // ─── Stuck-run reaping ────────────────────────────────────────────

    [Fact]
    public async Task A_run_orphaned_by_a_restart_is_failed_not_left_Running()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource();
        fixture.AppDbContext.JobSources.Add(source);
        fixture.AppDbContext.JobImportRuns.Add(new JobImportRun
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            SourceName = source.SourceName,
            Trigger = JobImportRunTrigger.Manual,
            Status = JobImportRunStatus.Running,
            StartedAtUtc = DateTime.UtcNow - JobImportService.BackgroundRunLimit - TimeSpan.FromMinutes(5)
        });
        await fixture.AppDbContext.SaveChangesAsync();

        var reaped = await BuildService(fixture, new JobImportQueue()).ReapStuckRunsAsync();

        reaped.IsSuccess.Should().BeTrue();
        reaped.Data.Should().Be(1);

        var run = fixture.AppDbContext.JobImportRuns.Single();
        run.Status.Should().Be(JobImportRunStatus.Failed);
        run.IsSuccess.Should().BeFalse();
        run.CompletedAtUtc.Should().NotBeNull();
        run.FailureMessage.Should().Contain("did not report back");
    }

    [Fact]
    public async Task A_recent_run_is_left_alone_by_the_reaper()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource();
        fixture.AppDbContext.JobSources.Add(source);
        fixture.AppDbContext.JobImportRuns.Add(new JobImportRun
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            SourceName = source.SourceName,
            Trigger = JobImportRunTrigger.Manual,
            Status = JobImportRunStatus.Running,
            StartedAtUtc = DateTime.UtcNow - TimeSpan.FromMinutes(1)
        });
        await fixture.AppDbContext.SaveChangesAsync();

        var reaped = await BuildService(fixture, new JobImportQueue()).ReapStuckRunsAsync();

        reaped.Data.Should().Be(0, "a crawl one minute in is simply still working");
        fixture.AppDbContext.JobImportRuns.Single().Status.Should().Be(JobImportRunStatus.Running);
    }

    [Fact]
    public async Task A_stale_run_cannot_block_a_source_forever()
    {
        // The failure this prevents: an app-pool recycle leaves a Running
        // row, the duplicate check sees it, and every future refresh of
        // that source is refused for good.
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource();
        fixture.AppDbContext.JobSources.Add(source);
        fixture.AppDbContext.JobImportRuns.Add(new JobImportRun
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            SourceName = source.SourceName,
            Trigger = JobImportRunTrigger.Manual,
            Status = JobImportRunStatus.Running,
            StartedAtUtc = DateTime.UtcNow - JobImportService.BackgroundRunLimit - TimeSpan.FromHours(2)
        });
        await fixture.AppDbContext.SaveChangesAsync();

        var queue = new JobImportQueue();
        var result = await BuildService(fixture, queue).QueueSourceRefreshAsync(source.Id, JobImportRunTrigger.Manual);

        result.IsSuccess.Should().BeTrue("the zombie must be reaped before the duplicate check runs");
        result.Data!.Status.Should().Be(JobImportRunStatus.Running);
        queue.PendingCount.Should().Be(1);
        fixture.AppDbContext.JobImportRuns.Count(r => r.Status == JobImportRunStatus.Failed).Should().Be(1);
    }

    // ─── Worker execution ─────────────────────────────────────────────

    [Fact]
    public async Task The_worker_finishes_the_queued_run_rather_than_creating_a_second_one()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource();
        fixture.AppDbContext.JobSources.Add(source);
        await fixture.AppDbContext.SaveChangesAsync();

        // The source refuses us. A failed crawl is still a completed run.
        var fetcher = new Mock<IJobSourceFetcher>();
        fetcher.Setup(f => f.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobFetchResult { IsSuccess = false, StatusCode = 403, FailureMessage = "Forbidden" });

        var queue = new JobImportQueue();
        var service = BuildService(fixture, queue, fetcher.Object);

        var queued = await service.QueueSourceRefreshAsync(source.Id, JobImportRunTrigger.Manual);
        await service.ExecuteQueuedRunAsync(source.Id, queued.Data!.Id);

        fixture.AppDbContext.JobImportRuns.Count().Should().Be(1, "the worker must adopt the queued row, not add another");

        var run = fixture.AppDbContext.JobImportRuns.Single();
        run.Id.Should().Be(queued.Data.Id);
        run.Status.Should().Be(JobImportRunStatus.Failed);
        run.CompletedAtUtc.Should().NotBeNull("a finished run must stop looking Running to the poller");
        run.FailureMessage.Should().Contain("Forbidden");
    }

    [Fact]
    public async Task The_worker_ignores_a_run_that_was_already_finished()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource();
        fixture.AppDbContext.JobSources.Add(source);
        await fixture.AppDbContext.SaveChangesAsync();

        var queue = new JobImportQueue();
        var service = BuildService(fixture, queue, Mock.Of<IJobSourceFetcher>(MockBehavior.Strict));

        var queued = await service.QueueSourceRefreshAsync(source.Id, JobImportRunTrigger.Manual);

        // The reaper failed it while it waited in the queue.
        var row = fixture.AppDbContext.JobImportRuns.Single();
        row.Status = JobImportRunStatus.Failed;
        row.CompletedAtUtc = DateTime.UtcNow;
        await fixture.AppDbContext.SaveChangesAsync();

        // Must be a no-op — a strict fetcher proves nothing was crawled.
        await service.ExecuteQueuedRunAsync(source.Id, queued.Data!.Id);

        fixture.AppDbContext.JobImportRuns.Single().Status.Should().Be(JobImportRunStatus.Failed);
    }

    [Fact]
    public async Task The_worker_records_a_deleted_source_instead_of_leaving_the_run_Running()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = NewSource();
        fixture.AppDbContext.JobSources.Add(source);
        await fixture.AppDbContext.SaveChangesAsync();

        var service = BuildService(fixture, new JobImportQueue());
        var queued = await service.QueueSourceRefreshAsync(source.Id, JobImportRunTrigger.Manual);

        fixture.AppDbContext.JobSources.Remove(fixture.AppDbContext.JobSources.Single());
        await fixture.AppDbContext.SaveChangesAsync();

        await service.ExecuteQueuedRunAsync(source.Id, queued.Data!.Id);

        var run = fixture.AppDbContext.JobImportRuns.Single();
        run.Status.Should().Be(JobImportRunStatus.Failed);
        run.FailureMessage.Should().Contain("deleted");
    }

    // ─── Queue mechanics ──────────────────────────────────────────────

    [Fact]
    public async Task The_queue_hands_items_to_the_reader_in_order()
    {
        var queue = new JobImportQueue();
        var first = new JobImportQueueItem(Guid.NewGuid(), Guid.NewGuid());
        var second = new JobImportQueueItem(Guid.NewGuid(), Guid.NewGuid());

        queue.TryEnqueue(first).Should().BeTrue();
        queue.TryEnqueue(second).Should().BeTrue();
        queue.PendingCount.Should().Be(2);

        // `break` out rather than cancelling: cancelling a channel read
        // throws by design, which would test the cancellation path, not
        // the ordering.
        var drained = new List<JobImportQueueItem>();
        await foreach (var item in queue.ReadAllAsync(CancellationToken.None))
        {
            drained.Add(item);
            if (drained.Count == 2) break;
        }

        drained.Should().Equal(first, second);
        queue.PendingCount.Should().Be(0);
    }
}

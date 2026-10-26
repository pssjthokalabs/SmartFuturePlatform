using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.API.Extensions;
using SmartFuture.API.HostedServices;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Jobs;

// The once-daily scheduled job import: registration, the 05:00 SAST
// slot, retries (max 5, backoff, only the sources that need it),
// exhaustion, restart safety, cross-instance claims and the guarantee
// that repeated imports never duplicate a job.
//
// Execution tests use the REAL JobImportService (fetch → extract →
// fingerprint upsert) on SQLite with a scripted fetcher, so "no duplicate
// jobs" is proven against the real unique index, not a mock.
//
// CLOCK NOTE: the importer stamps per-source rows with DateTime.UtcNow,
// so these tests anchor the daily slot one minute before the real clock
// (ScheduleStartingJustNow) and move the scheduler's "now" forward from
// there. Every retry lands well inside the same 24-hour slot.
public class JobImportDailySchedulerTests
{
    private const string Sast = "Africa/Johannesburg";

    // ─── Schedule policy (pure) ───────────────────────────────────────

    [Fact]
    public void Slot_starts_at_0500_SAST_which_is_0300_UTC()
    {
        var slot = JobImportDailySchedule.CurrentSlot(Utc(2026, 10, 26, 3, 30), new JobImportScheduleSettings());

        slot.Date.Should().Be(new DateOnly(2026, 10, 26));
        slot.StartUtc.Should().Be(Utc(2026, 10, 26, 3, 0));
        slot.NextStartUtc.Should().Be(Utc(2026, 10, 27, 3, 0));
    }

    [Fact]
    public void Before_0500_SAST_the_current_slot_is_still_yesterdays()
    {
        // 02:59 UTC = 04:59 SAST.
        var slot = JobImportDailySchedule.CurrentSlot(Utc(2026, 10, 26, 2, 59), new JobImportScheduleSettings());

        slot.Date.Should().Be(new DateOnly(2026, 10, 25));
        slot.StartUtc.Should().Be(Utc(2026, 10, 25, 3, 0));
    }

    [Fact]
    public void Invalid_schedule_settings_fall_back_to_0500_SAST_instead_of_stopping_the_importer()
    {
        var settings = new JobImportScheduleSettings { TimeZone = "Mars/Olympus_Mons", DailyRunTime = "25:99" };

        var slot = JobImportDailySchedule.CurrentSlot(Utc(2026, 10, 26, 12, 0), settings);

        slot.StartUtc.Should().Be(Utc(2026, 10, 26, 3, 0));
    }

    [Fact]
    public void Retry_backoff_is_exponential_and_capped()
    {
        var settings = new JobImportScheduleSettings();

        Enumerable.Range(1, 5).Select(n => JobImportDailySchedule.RetryDelay(n, settings).TotalMinutes)
            .Should().Equal(5, 10, 20, 40, 60);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(9, 5)]
    [InlineData(2, 2)]
    [InlineData(-1, 0)]
    public void Retries_are_capped_at_five(int configured, int expected)
    {
        JobImportDailySchedule.MaxRetries(new JobImportScheduleSettings { MaxRetryAttempts = configured }).Should().Be(expected);
    }

    [Fact]
    public void Attempt_ids_are_the_same_on_every_instance_and_unique_per_slot_and_attempt()
    {
        var day = new DateOnly(2026, 10, 26);

        JobImportDailySchedule.AttemptId(day, 1).Should().Be(JobImportDailySchedule.AttemptId(day, 1));
        JobImportDailySchedule.AttemptId(day, 2).Should().NotBe(JobImportDailySchedule.AttemptId(day, 1));
        JobImportDailySchedule.AttemptId(day.AddDays(1), 1).Should().NotBe(JobImportDailySchedule.AttemptId(day, 1));
    }

    [Theory]
    [InlineData(null, "The source timed out. It may be slow or blocking automated requests.", JobImportFailureKind.Transient)]
    [InlineData(null, "Could not reach the source: No such host is known.", JobImportFailureKind.Transient)]
    [InlineData(503, "The source returned HTTP 503 (Service Unavailable).", JobImportFailureKind.Transient)]
    [InlineData(429, "The source rate-limited us (429).", JobImportFailureKind.Transient)]
    [InlineData(200, "Stopped after 1800s before the crawl finished.", JobImportFailureKind.Transient)]
    [InlineData(200, "Unexpected error: database blip", JobImportFailureKind.Transient)]
    [InlineData(403, "The source refused the request (403).", JobImportFailureKind.Permanent)]
    [InlineData(404, "The source URL returned 404.", JobImportFailureKind.Permanent)]
    [InlineData(null, "Source URL must be a valid http(s) address.", JobImportFailureKind.Permanent)]
    [InlineData(200, "The page was fetched but no job listings could be extracted from it.", JobImportFailureKind.Permanent)]
    public void Failures_are_classified_as_retryable_or_permanent(int? status, string message, JobImportFailureKind expected)
    {
        JobImportFailureClassifier.Classify(status, message).Should().Be(expected);
    }

    [Fact]
    public void Credentials_are_masked_before_an_error_is_logged_or_stored()
    {
        JobImportDailySchedule.RedactSecrets("Server=db;User ID=sf;Password=hunter2;Encrypt=True").Should().Be("Server=db;User ID=sf;Password=[REDACTED];Encrypt=True");
        JobImportDailySchedule.RedactSecrets("GET https://feed.example/jobs?api_key=abc123&page=2 failed").Should().Be("GET https://feed.example/jobs?api_key=[REDACTED]&page=2 failed");
    }

    [Fact]
    public void The_worker_sleeps_until_the_next_decision_but_between_30s_and_15min()
    {
        var now = Utc(2026, 10, 26, 3, 0);

        JobImportHostedService.NextTickDelay(now.AddMinutes(5), now).Should().Be(TimeSpan.FromMinutes(5));
        JobImportHostedService.NextTickDelay(now.AddHours(20), now).Should().Be(TimeSpan.FromMinutes(15));
        JobImportHostedService.NextTickDelay(now.AddSeconds(-10), now).Should().Be(TimeSpan.FromSeconds(30));
    }

    // ─── Registration ─────────────────────────────────────────────────

    [Fact]
    public void The_daily_import_starts_with_the_API_and_needs_no_admin()
    {
        var services = new ServiceCollection();
        services.AddSmartFutureBackgroundServices();
        services.AddAuthServices();

        services.Should().Contain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobImportHostedService),
            "the worker is a hosted service, so it starts with the deployed API");
        services.Should().Contain(d => d.ServiceType == typeof(IJobImportScheduler) && d.ImplementationType == typeof(JobImportScheduler) && d.Lifetime == ServiceLifetime.Scoped);
    }

    // ─── Execution ────────────────────────────────────────────────────

    [Fact]
    public async Task A_due_slot_imports_every_scheduled_source_and_records_the_attempt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var a = await AddSourceAsync(fixture, "Board A");
        var b = await AddSourceAsync(fixture, "Board B");
        var fetcher = new ScriptedFetcher().Always(a.SourceUrl, JobPage("Warehouse Supervisor")).Always(b.SourceUrl, JobPage("Forklift Driver"));
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher);

        var tick = await scheduler.RunDueAsync(DateTime.UtcNow);

        tick.State.Should().Be(JobImportScheduleState.Succeeded);
        tick.AttemptExecuted.Should().Be(1);

        var attempt = BatchRows(fixture).Single();
        attempt.SourceId.Should().BeNull();
        attempt.SourceName.Should().Be(JobImportDailySchedule.BatchRunName);
        attempt.Status.Should().Be(JobImportRunStatus.Succeeded);
        attempt.IsSuccess.Should().BeTrue();
        attempt.JobsCreated.Should().Be(2);
        attempt.CompletedAtUtc.Should().NotBeNull();
        attempt.Notes.Should().Contain("Attempt 1 of 6");

        fixture.AppDbContext.JobImportRuns.Count(r => r.SourceId != null && r.Trigger == JobImportRunTrigger.Scheduled).Should().Be(2);
        fixture.AppDbContext.JobOpportunities.Count().Should().Be(2);
    }

    [Fact]
    public async Task Nothing_runs_while_automatic_import_is_switched_off()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Board A");
        var fetcher = new ScriptedFetcher().Always(source.SourceUrl, JobPage("Cashier"));
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, autoImportEnabled: false);

        var tick = await scheduler.RunDueAsync(DateTime.UtcNow);

        tick.State.Should().Be(JobImportScheduleState.Disabled);
        fetcher.Calls.Should().BeEmpty();
        fixture.AppDbContext.JobImportRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_scheduled_importing_on_later_the_same_day_runs_todays_missed_import_on_the_next_tick()
    {
        // Production on 2026-10-26: the switch was OFF at 05:00, so nothing ran;
        // an Admin ticks it later that morning.
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Board A");
        var fetcher = new ScriptedFetcher().Always(source.SourceUrl, JobPage("Cashier"));
        var jobSettings = new JobModuleSettings { JobsModuleEnabled = true, AutoImportEnabled = false };
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, ScheduleStartingJustNow(), moduleSettings: jobSettings);
        var now = DateTime.UtcNow;

        var whileOff = await scheduler.RunDueAsync(now);
        Assert.Equal(JobImportScheduleState.Disabled, whileOff.State);
        Assert.Empty(fetcher.Calls);
        Assert.Empty(BatchRows(fixture));

        jobSettings.AutoImportEnabled = true; // Admin → Job Settings → Scheduled importing, saved
        var afterSwitchOn = await scheduler.RunDueAsync(now.AddHours(3));

        Assert.Equal(1, afterSwitchOn.AttemptExecuted);
        Assert.Equal(JobImportScheduleState.Succeeded, afterSwitchOn.State);
        Assert.Equal(1, fetcher.CallsTo(source.SourceUrl));
        Assert.Equal(1, fixture.AppDbContext.JobOpportunities.Count());
    }

    [Fact]
    public async Task Switching_scheduled_importing_off_stops_pending_retries_and_switching_it_back_on_resumes_them()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Flaky Board");
        var fetcher = new ScriptedFetcher()
            .First(source.SourceUrl, JobFetchResult.Failure("The source returned HTTP 503 (Service Unavailable).", 503))
            .Always(source.SourceUrl, JobPage("Cashier"));
        var jobSettings = new JobModuleSettings { JobsModuleEnabled = true, AutoImportEnabled = true };
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, ScheduleStartingJustNow(), moduleSettings: jobSettings);
        var now = DateTime.UtcNow;

        var first = await scheduler.RunDueAsync(now);
        Assert.Equal(JobImportScheduleState.RetryScheduled, first.State);

        jobSettings.AutoImportEnabled = false;
        var whileOff = await scheduler.RunDueAsync(first.NextCheckUtc);
        Assert.Equal(JobImportScheduleState.Disabled, whileOff.State);
        Assert.Null(whileOff.AttemptExecuted);
        Assert.Equal(1, fetcher.CallsTo(source.SourceUrl));

        jobSettings.AutoImportEnabled = true;
        var resumed = await scheduler.RunDueAsync(first.NextCheckUtc.AddMinutes(1));
        Assert.Equal(2, resumed.AttemptExecuted);
        Assert.Equal(JobImportScheduleState.Succeeded, resumed.State);
    }

    [Fact]
    public async Task Turning_off_the_whole_Jobs_module_also_stops_the_daily_import()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Board A");
        var fetcher = new ScriptedFetcher().Always(source.SourceUrl, JobPage("Cashier"));
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, ScheduleStartingJustNow(),
            moduleSettings: new JobModuleSettings { JobsModuleEnabled = false, AutoImportEnabled = true });

        var tick = await scheduler.RunDueAsync(DateTime.UtcNow);

        Assert.Equal(JobImportScheduleState.Disabled, tick.State);
        Assert.Empty(fetcher.Calls);
    }

    [Fact]
    public async Task Manual_only_and_inactive_sources_are_not_part_of_the_daily_run()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var manualOnly = await AddSourceAsync(fixture, "No interval", crawlFrequencyMinutes: null);
        var inactive = await AddSourceAsync(fixture, "Disabled", isActive: false);
        var fetcher = new ScriptedFetcher().Always(manualOnly.SourceUrl, JobPage("A")).Always(inactive.SourceUrl, JobPage("B"));
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher);

        var tick = await scheduler.RunDueAsync(DateTime.UtcNow);

        tick.State.Should().Be(JobImportScheduleState.Succeeded);
        fetcher.Calls.Should().BeEmpty();
        BatchRows(fixture).Single().Notes.Should().Contain("No scheduled sources were due");
    }

    [Fact]
    public async Task A_restart_never_runs_the_same_day_twice()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Board A");
        var fetcher = new ScriptedFetcher().Always(source.SourceUrl, JobPage("Cashier"));
        var schedule = ScheduleStartingJustNow();
        var now = DateTime.UtcNow;

        await Build(fixture.AppDbContext, fetcher, schedule).Scheduler.RunDueAsync(now);

        // The API restarts: a brand-new process (fresh context, fresh
        // scheduler) wakes up later the same day.
        await using var restarted = fixture.CreateSiblingContext();
        var tick = await Build(restarted, fetcher, schedule).Scheduler.RunDueAsync(now.AddHours(3));

        tick.State.Should().Be(JobImportScheduleState.Succeeded);
        tick.AttemptExecuted.Should().BeNull();
        fetcher.CallsTo(source.SourceUrl).Should().Be(1);
        BatchRows(fixture).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_after_backoff_for_that_source_only_and_then_succeeds()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var healthy = await AddSourceAsync(fixture, "Healthy Board");
        var flaky = await AddSourceAsync(fixture, "Flaky Board");
        var fetcher = new ScriptedFetcher()
            .Always(healthy.SourceUrl, JobPage("Warehouse Supervisor"))
            .First(flaky.SourceUrl, JobFetchResult.Failure("The source returned HTTP 503 (Service Unavailable).", 503))
            .Always(flaky.SourceUrl, JobPage("Forklift Driver"));
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, ScheduleStartingJustNow());
        var now = DateTime.UtcNow;

        var first = await scheduler.RunDueAsync(now);

        first.State.Should().Be(JobImportScheduleState.RetryScheduled);
        var firstAttempt = BatchRows(fixture).Single();
        firstAttempt.Status.Should().Be(JobImportRunStatus.PartiallySucceeded);
        firstAttempt.FailureMessage.Should().Contain("Retry 1 of 5").And.Contain("Flaky Board");
        first.NextCheckUtc.Should().BeCloseTo(firstAttempt.CompletedAtUtc!.Value.AddMinutes(5), TimeSpan.FromSeconds(1));

        // Backoff not elapsed yet: no attempt, no crawl.
        var early = await scheduler.RunDueAsync(now.AddMinutes(2));
        early.State.Should().Be(JobImportScheduleState.RetryScheduled);
        early.AttemptExecuted.Should().BeNull();

        var retry = await scheduler.RunDueAsync(first.NextCheckUtc);

        retry.AttemptExecuted.Should().Be(2);
        retry.State.Should().Be(JobImportScheduleState.Succeeded);
        fetcher.CallsTo(healthy.SourceUrl).Should().Be(1, "a source that already succeeded today is never re-crawled by a retry");
        fetcher.CallsTo(flaky.SourceUrl).Should().Be(2);
        fixture.AppDbContext.JobOpportunities.Count().Should().Be(2, "a partial import followed by a retry must not duplicate jobs");
    }

    [Fact]
    public async Task Retries_stop_after_five_and_the_run_is_marked_failed_then_tomorrow_runs_normally()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Down Board");
        var fetcher = new ScriptedFetcher().Always(source.SourceUrl, JobFetchResult.Failure("The source returned HTTP 503 (Service Unavailable).", 503));
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, ScheduleStartingJustNow());

        var now = DateTime.UtcNow;
        var tick = await scheduler.RunDueAsync(now);
        while (tick.State == JobImportScheduleState.RetryScheduled)
            tick = await scheduler.RunDueAsync(tick.NextCheckUtc);

        tick.State.Should().Be(JobImportScheduleState.Failed);
        tick.AttemptsUsed.Should().Be(6, "one attempt plus at most five retries");
        fetcher.CallsTo(source.SourceUrl).Should().Be(6);

        var last = BatchRows(fixture).OrderByDescending(r => r.StartedAtUtc).First();
        last.Status.Should().Be(JobImportRunStatus.Failed);
        last.IsSuccess.Should().BeFalse();
        last.FailureMessage.Should().Contain("Retries exhausted after 6 attempt(s)").And.Contain("Down Board");

        // Exhausted means exhausted — later ticks the same day do nothing.
        var later = await scheduler.RunDueAsync(tick.NextCheckUtc.AddHours(-1));
        later.AttemptExecuted.Should().BeNull();
        fetcher.CallsTo(source.SourceUrl).Should().Be(6);

        // The next day's slot starts fresh.
        var tomorrow = await scheduler.RunDueAsync(now.AddDays(1));
        tomorrow.SlotDate.Should().Be(tick.SlotDate.AddDays(1));
        tomorrow.AttemptExecuted.Should().Be(1);
        fetcher.CallsTo(source.SourceUrl).Should().Be(7);
    }

    [Fact]
    public async Task A_permanent_failure_is_not_retried_today()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var healthy = await AddSourceAsync(fixture, "Healthy Board");
        var blocked = await AddSourceAsync(fixture, "Blocking Board");
        var fetcher = new ScriptedFetcher()
            .Always(healthy.SourceUrl, JobPage("Cashier"))
            .Always(blocked.SourceUrl, JobFetchResult.Failure("The source refused the request (403). This site likely blocks automated access.", 403));
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, ScheduleStartingJustNow());
        var now = DateTime.UtcNow;

        var tick = await scheduler.RunDueAsync(now);
        var later = await scheduler.RunDueAsync(now.AddHours(2));

        tick.State.Should().Be(JobImportScheduleState.CompletedWithErrors);
        later.AttemptExecuted.Should().BeNull();
        fetcher.CallsTo(blocked.SourceUrl).Should().Be(1);
        BatchRows(fixture).Single().FailureMessage.Should().Contain("Blocking Board").And.Contain("tomorrow");
    }

    [Fact]
    public async Task Repeated_imports_update_existing_jobs_and_never_duplicate_them()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Board A");
        var fetcher = new ScriptedFetcher().Always(source.SourceUrl, JobPage("Warehouse Supervisor"));
        var (scheduler, import) = Build(fixture.AppDbContext, fetcher, ScheduleStartingJustNow());

        await scheduler.RunDueAsync(DateTime.UtcNow);
        var manual = await import.RunAllAsync(JobImportRunTrigger.Manual, onlyDue: false);
        var manualAgain = await import.RunAllAsync(JobImportRunTrigger.Manual, onlyDue: false);

        fixture.AppDbContext.JobOpportunities.Count().Should().Be(1);
        manual.Data!.JobsCreated.Should().Be(0);
        manual.Data.JobsUpdated.Should().Be(1);
        manualAgain.Data!.JobsUpdated.Should().Be(1);
    }

    // ─── Overlap protection ───────────────────────────────────────────

    [Fact]
    public async Task Two_instances_racing_for_the_same_attempt_only_one_wins()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        await using var otherInstance = fixture.CreateSiblingContext();
        var schedule = ScheduleStartingJustNow();
        var now = DateTime.UtcNow;
        var slot = JobImportDailySchedule.CurrentSlot(now, schedule);

        var mine = await Build(fixture.AppDbContext, new ScriptedFetcher(), schedule).Scheduler.TryClaimAttemptAsync(slot, 1, now);
        var theirs = await Build(otherInstance, new ScriptedFetcher(), schedule).Scheduler.TryClaimAttemptAsync(slot, 1, now);

        mine.Should().NotBeNull();
        theirs.Should().BeNull("the attempt's primary key is already taken by the first instance");
        BatchRows(fixture).Should().HaveCount(1);
    }

    [Fact]
    public async Task An_attempt_running_on_another_instance_blocks_this_one()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Board A");
        var fetcher = new ScriptedFetcher().Always(source.SourceUrl, JobPage("Cashier"));
        var schedule = ScheduleStartingJustNow();
        var now = DateTime.UtcNow;

        await using var otherInstance = fixture.CreateSiblingContext();
        await Build(otherInstance, fetcher, schedule).Scheduler.TryClaimAttemptAsync(JobImportDailySchedule.CurrentSlot(now, schedule), 1, now);

        var tick = await Build(fixture.AppDbContext, fetcher, schedule).Scheduler.RunDueAsync(now.AddMinutes(5));

        tick.State.Should().Be(JobImportScheduleState.Running);
        fetcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_attempt_whose_process_died_is_marked_interrupted_and_the_slot_is_retried()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Board A");
        var fetcher = new ScriptedFetcher().Always(source.SourceUrl, JobPage("Cashier"));
        var schedule = ScheduleStartingJustNow();
        var now = DateTime.UtcNow;
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, schedule);

        // Claimed, then the process vanished: no heartbeat ever again.
        await scheduler.TryClaimAttemptAsync(JobImportDailySchedule.CurrentSlot(now, schedule), 1, now);

        var noticed = await scheduler.RunDueAsync(now.AddHours(1));
        noticed.State.Should().Be(JobImportScheduleState.RetryScheduled);
        var dead = BatchRows(fixture).Single();
        dead.Status.Should().Be(JobImportRunStatus.Failed);
        dead.FailureMessage.Should().Contain("stopped reporting progress");

        var retried = await scheduler.RunDueAsync(noticed.NextCheckUtc);
        retried.AttemptExecuted.Should().Be(2);
        retried.State.Should().Be(JobImportScheduleState.Succeeded);
    }

    [Fact]
    public async Task A_shutdown_mid_import_is_recorded_as_interrupted_and_retried_after_restart()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Board A");
        using var shutdown = new CancellationTokenSource();
        var fetcher = new ScriptedFetcher()
            // What HttpJobSourceFetcher does when the host stops mid-request.
            .First(source.SourceUrl, () =>
            {
                shutdown.Cancel();
                return JobFetchResult.Failure("The import run was cancelled.");
            })
            .Always(source.SourceUrl, JobPage("Cashier"));
        var schedule = ScheduleStartingJustNow();
        var now = DateTime.UtcNow;

        await Build(fixture.AppDbContext, fetcher, schedule).Scheduler.RunDueAsync(now, shutdown.Token);

        var interrupted = BatchRows(fixture).Single();
        interrupted.Status.Should().Be(JobImportRunStatus.Failed);
        interrupted.CompletedAtUtc.Should().NotBeNull("the attempt must not be left Running for the next process to wait on");
        interrupted.FailureMessage.Should().Contain("Interrupted");

        await using var restarted = fixture.CreateSiblingContext();
        var tick = await Build(restarted, fetcher, schedule).Scheduler.RunDueAsync(now.AddMinutes(6));

        tick.AttemptExecuted.Should().Be(2);
        tick.State.Should().Be(JobImportScheduleState.Succeeded);
        fixture.AppDbContext.JobOpportunities.Count().Should().Be(1);
    }

    [Fact]
    public async Task The_daily_run_never_crawls_a_source_a_manual_refresh_is_already_crawling()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var a = await AddSourceAsync(fixture, "Board A");
        var b = await AddSourceAsync(fixture, "Board B");
        var manualRun = new JobImportRun { SourceId = b.Id, SourceName = b.SourceName, Trigger = JobImportRunTrigger.Manual, Status = JobImportRunStatus.Running, StartedAtUtc = DateTime.UtcNow };
        fixture.AppDbContext.JobImportRuns.Add(manualRun);
        await fixture.AppDbContext.SaveChangesAsync();

        var fetcher = new ScriptedFetcher().Always(a.SourceUrl, JobPage("Cashier")).Always(b.SourceUrl, JobPage("Driver"));
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, ScheduleStartingJustNow());
        var now = DateTime.UtcNow;

        var first = await scheduler.RunDueAsync(now);

        fetcher.CallsTo(b.SourceUrl).Should().Be(0, "two crawls of one source must never run at once");
        first.State.Should().Be(JobImportScheduleState.RetryScheduled);

        // The manual refresh finishes successfully before the retry is due —
        // the source is fresh, so the retry has nothing left to do.
        manualRun.Status = JobImportRunStatus.Succeeded;
        manualRun.IsSuccess = true;
        manualRun.CompletedAtUtc = DateTime.UtcNow;
        await fixture.AppDbContext.SaveChangesAsync();

        var retry = await scheduler.RunDueAsync(first.NextCheckUtc);

        retry.State.Should().Be(JobImportScheduleState.Succeeded);
        retry.AttemptExecuted.Should().BeNull();
        fetcher.CallsTo(b.SourceUrl).Should().Be(0);
    }

    [Fact]
    public async Task Refresh_all_skips_a_source_that_is_already_being_imported()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var busy = await AddSourceAsync(fixture, "Busy Board");
        var idle = await AddSourceAsync(fixture, "Idle Board");
        fixture.AppDbContext.JobImportRuns.Add(new JobImportRun { SourceId = busy.Id, SourceName = busy.SourceName, Status = JobImportRunStatus.Running, StartedAtUtc = DateTime.UtcNow });
        await fixture.AppDbContext.SaveChangesAsync();

        var fetcher = new ScriptedFetcher().Always(busy.SourceUrl, JobPage("A")).Always(idle.SourceUrl, JobPage("B"));
        var (_, import) = Build(fixture.AppDbContext, fetcher);

        var result = await import.RunAllAsync(JobImportRunTrigger.Manual, onlyDue: false);

        result.IsSuccess.Should().BeTrue();
        result.Data!.SourcesAttempted.Should().Be(1);
        result.Data.SourcesSkippedInFlight.Should().Be(1);
        result.Message.Should().Contain("already running");
        fetcher.CallsTo(busy.SourceUrl).Should().Be(0);
    }

    [Fact]
    public async Task The_per_source_stuck_run_reaper_leaves_daily_attempts_alone()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Board A");
        var longAgo = DateTime.UtcNow - JobImportService.BackgroundRunLimit - TimeSpan.FromMinutes(10);
        fixture.AppDbContext.JobImportRuns.AddRange(
            new JobImportRun { SourceId = source.Id, SourceName = source.SourceName, Status = JobImportRunStatus.Running, StartedAtUtc = longAgo },
            new JobImportRun { SourceName = JobImportDailySchedule.BatchRunName, Trigger = JobImportRunTrigger.DailySchedule, Status = JobImportRunStatus.Running, StartedAtUtc = longAgo });
        await fixture.AppDbContext.SaveChangesAsync();

        var (_, import) = Build(fixture.AppDbContext, new ScriptedFetcher());
        var reaped = await import.ReapStuckRunsAsync();

        reaped.Data.Should().Be(1, "a daily attempt legitimately outlives one source's limit; it is reaped by heartbeat instead");
        BatchRows(fixture).Single().Status.Should().Be(JobImportRunStatus.Running);
    }

    // ─── Admin status ─────────────────────────────────────────────────

    [Fact]
    public async Task Status_reports_the_schedule_the_pending_retry_and_the_last_error()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var source = await AddSourceAsync(fixture, "Down Board");
        var fetcher = new ScriptedFetcher().Always(source.SourceUrl, JobFetchResult.Failure("The source timed out. It may be slow or blocking automated requests."));
        var schedule = ScheduleStartingJustNow();
        var (scheduler, _) = Build(fixture.AppDbContext, fetcher, schedule);
        var now = DateTime.UtcNow;

        await scheduler.RunDueAsync(now);
        var status = await scheduler.GetStatusAsync(now.AddMinutes(1));

        status.IsSuccess.Should().BeTrue();
        var dto = status.Data!;
        dto.AutomaticImportEnabled.Should().BeTrue();
        dto.DailyRunTime.Should().Be(schedule.DailyRunTime);
        dto.TimeZone.Should().Contain("UTC+02:00");
        dto.MaxRetryAttempts.Should().Be(5);
        dto.State.Should().Be(JobImportScheduleState.RetryScheduled);
        dto.NextRetryAtUtc.Should().NotBeNull();
        dto.AttemptsUsed.Should().Be(1);
        dto.LastAttempt!.IsSuccess.Should().BeFalse();
        dto.LastSuccessfulRun.Should().BeNull();
        dto.LastError.Should().Contain("Down Board");
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static DateTime Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // A schedule whose slot began one minute ago (see CLOCK NOTE above).
    private static JobImportScheduleSettings ScheduleStartingJustNow()
    {
        var zone = JobImportDailySchedule.ResolveTimeZone(Sast);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).AddMinutes(-1);
        return new JobImportScheduleSettings { DailyRunTime = local.ToString("HH:mm"), TimeZone = Sast };
    }

    /// <param name="moduleSettings">The Job Settings row the scheduler re-reads every tick — pass one in to flip "Scheduled importing" mid-test.</param>
    private static (JobImportScheduler Scheduler, JobImportService Import) Build(IAppDbContext db, IJobSourceFetcher fetcher, JobImportScheduleSettings? schedule = null,
        bool autoImportEnabled = true, JobModuleSettings? moduleSettings = null)
    {
        var jobService = new Mock<IJobOpportunityService>();
        jobService.Setup(s => s.ExpireClosedJobsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Result<int>.Success(0));

        var settings = new Mock<IJobSettingsService>();
        var row = moduleSettings ?? new JobModuleSettings { JobsModuleEnabled = true, AutoImportEnabled = autoImportEnabled };
        settings.Setup(s => s.GetOrCreateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(row);

        var import = new JobImportService(db, fetcher, new JobContentExtractor(), jobService.Object, new JobImportQueue(), Mock.Of<IAuditService>(),
            Mock.Of<ICurrentUserService>(), NullLogger<JobImportService>.Instance);
        var scheduler = new JobImportScheduler(db, import, settings.Object, jobService.Object, Options.Create(schedule ?? ScheduleStartingJustNow()),
            NullLogger<JobImportScheduler>.Instance);

        return (scheduler, import);
    }

    // SourceUrl carries a unique index, so every source gets its own.
    private static async Task<JobSource> AddSourceAsync(SqliteTestDbFixture fixture, string name, int? crawlFrequencyMinutes = 1440, bool isActive = true)
    {
        var source = new JobSource
        {
            Id = Guid.NewGuid(),
            SourceName = name,
            SourceUrl = $"https://board.co.za/{Guid.NewGuid():N}/jobs/",
            SourceType = JobSourceType.HtmlPage,
            IsActive = isActive,
            CrawlFrequencyMinutes = crawlFrequencyMinutes,
            MaxJobsPerRun = 5,
            MaxPagesPerRun = 1
        };
        fixture.AppDbContext.JobSources.Add(source);
        await fixture.AppDbContext.SaveChangesAsync();
        return source;
    }

    private static List<JobImportRun> BatchRows(SqliteTestDbFixture fixture)
        => fixture.AppDbContext.JobImportRuns.Where(r => r.Trigger == JobImportRunTrigger.DailySchedule).ToList();

    // A single job DETAIL page (JSON-LD JobPosting) — the importer
    // extracts exactly one job from it.
    private static JobFetchResult JobPage(string title) => new()
    {
        IsSuccess = true,
        StatusCode = 200,
        ContentType = "text/html",
        Content = $$"""
            <html><head>
            <script type="application/ld+json">
            {
              "@context": "https://schema.org",
              "@type": "JobPosting",
              "title": "{{title}}",
              "description": "<p>Lead the night shift team at our Durban depot.</p>",
              "hiringOrganization": { "@type": "Organization", "name": "Acme Logistics" },
              "jobLocation": { "@type": "Place", "address": { "addressLocality": "Durban", "addressRegion": "KwaZulu-Natal" } }
            }
            </script>
            </head><body>listing</body></html>
            """
    };

    // Per-URL scripted responses: First(...) results are served once, in
    // order; Always(...) is served after that.
    private sealed class ScriptedFetcher : IJobSourceFetcher
    {
        private readonly Dictionary<string, Queue<Func<JobFetchResult>>> _first = new();
        private readonly Dictionary<string, Func<JobFetchResult>> _always = new();

        public List<string> Calls { get; } = new();

        public ScriptedFetcher Always(string url, JobFetchResult result)
        {
            _always[url] = () => result;
            return this;
        }

        public ScriptedFetcher First(string url, JobFetchResult result) => First(url, () => result);

        public ScriptedFetcher First(string url, Func<JobFetchResult> result)
        {
            if (!_first.TryGetValue(url, out var queue)) _first[url] = queue = new Queue<Func<JobFetchResult>>();
            queue.Enqueue(result);
            return this;
        }

        public int CallsTo(string url) => Calls.Count(c => c == url);

        public Task<JobFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
        {
            Calls.Add(url);
            if (_first.TryGetValue(url, out var queue) && queue.Count > 0) return Task.FromResult(queue.Dequeue()());
            return Task.FromResult(_always.TryGetValue(url, out var always) ? always() : JobFetchResult.Failure("No script for this URL.", 404));
        }
    }
}

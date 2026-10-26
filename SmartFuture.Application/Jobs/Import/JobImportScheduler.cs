using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs.Import;

// The once-daily scheduled import (05:00 SAST by default).
//
// EVERYTHING IS DECIDED FROM THE DATABASE, so a restart, an IIS
// app-pool recycle or a second API instance changes nothing:
//
//   • Each attempt is a JobImportRuns row (Trigger = DailySchedule, no
//     SourceId) whose primary key is derived from the slot date and the
//     attempt number. Claiming an attempt = inserting that row. Two
//     instances racing for the same attempt collide on the primary key
//     and exactly one wins — the same claim-by-key idiom JobSettingsService
//     already uses for its singleton row. No lock table, no migration.
//   • A running attempt touches its row after every source (UpdatedAtUtc
//     is the heartbeat). A row that stops being touched belonged to a
//     process that died; it is marked interrupted and the slot moves on.
//   • Which sources still need work is read back from the per-source run
//     rows in the slot's window, so a retry crawls ONLY the sources that
//     failed for a retryable reason — never the ones that already
//     succeeded — and the decision survives a restart.
//
// Retrying is two-level on purpose. A source that failed transiently
// (timeout, 5xx, 429, interrupted) is retried; one that failed
// permanently (invalid URL, 403/404, unparseable page) is not retried
// today. If the attempt itself blew up (database unreachable) every
// source it did not finish is still pending, so the next attempt resumes
// where it stopped. Duplicate jobs are impossible either way: every
// listing is upserted by its unique fingerprint.
public class JobImportScheduler : IJobImportScheduler
{
    // How soon to look again while another instance runs an attempt.
    private static readonly TimeSpan RunningRecheck = TimeSpan.FromMinutes(5);

    private const int MaxNotesLength = 3500;
    private const int MaxFailureLength = 2000;

    private readonly IAppDbContext _dbContext;
    private readonly IJobImportService _importService;
    private readonly IJobSettingsService _settingsService;
    private readonly IJobOpportunityService _jobService;
    private readonly JobImportScheduleSettings _schedule;
    private readonly ILogger<JobImportScheduler> _logger;

    public JobImportScheduler(IAppDbContext dbContext, IJobImportService importService, IJobSettingsService settingsService, IJobOpportunityService jobService,
        IOptions<JobImportScheduleSettings> schedule, ILogger<JobImportScheduler> logger)
    {
        _dbContext = dbContext;
        _importService = importService;
        _settingsService = settingsService;
        _jobService = jobService;
        _schedule = schedule.Value;
        _logger = logger;
    }

    private int MaxAttempts => 1 + JobImportDailySchedule.MaxRetries(_schedule);

    // ─── Tick ─────────────────────────────────────────────────────────

    public async Task<JobImportScheduleTickResult> RunDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var slot = JobImportDailySchedule.CurrentSlot(nowUtc, _schedule);

        var settings = await _settingsService.GetOrCreateAsync(cancellationToken);
        if (!JobImportSchedulingGate.ShouldRunScheduledImport(settings))
            return new JobImportScheduleTickResult(JobImportScheduleState.Disabled, slot.Date, 0, null, slot.NextStartUtc);

        var attempts = await LoadAttemptsAsync(slot, tracked: true, cancellationToken);
        if (!await FailAbandonedAttemptsAsync(attempts, nowUtc, cancellationToken))
            return new JobImportScheduleTickResult(JobImportScheduleState.Running, slot.Date, attempts.Count, null, nowUtc + RunningRecheck);

        var evaluation = await EvaluateAsync(slot, attempts, nowUtc, cancellationToken);
        if (evaluation.State != JobImportScheduleState.Due)
            return new JobImportScheduleTickResult(evaluation.State, slot.Date, attempts.Count, null, evaluation.NextCheckUtc);

        var attemptNumber = attempts.Count == 0 ? 1 : attempts.Max(a => a.Number) + 1;
        var run = await TryClaimAttemptAsync(slot, attemptNumber, nowUtc, cancellationToken);
        if (run is null)
            return new JobImportScheduleTickResult(JobImportScheduleState.Running, slot.Date, attempts.Count, null, nowUtc + RunningRecheck);

        await ExecuteAttemptAsync(slot, run, attemptNumber, nowUtc, cancellationToken);
        attempts.Add(new Attempt(attemptNumber, run));

        // Shutting down: the attempt is already recorded as interrupted and
        // the next process picks the slot up. Nothing more to decide here.
        if (cancellationToken.IsCancellationRequested)
            return new JobImportScheduleTickResult(JobImportScheduleState.RetryScheduled, slot.Date, attempts.Count, attemptNumber, nowUtc + RunningRecheck);

        var after = await EvaluateAsync(slot, attempts, run.CompletedAtUtc ?? nowUtc, CancellationToken.None);
        await RecordOutcomeAsync(slot, run, attemptNumber, after);

        return new JobImportScheduleTickResult(after.State, slot.Date, attempts.Count, attemptNumber, after.NextCheckUtc);
    }

    // ─── Status (read-only) ───────────────────────────────────────────

    public async Task<Result<JobImportScheduleStatusDto>> GetStatusAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            var slot = JobImportDailySchedule.CurrentSlot(nowUtc, _schedule);
            var settings = await _settingsService.GetOrCreateAsync(cancellationToken);
            var enabled = JobImportSchedulingGate.ShouldRunScheduledImport(settings);

            var attempts = await LoadAttemptsAsync(slot, tracked: false, cancellationToken);
            var evaluation = enabled
                ? await EvaluateAsync(slot, attempts, nowUtc, cancellationToken)
                : new SlotEvaluation(JobImportScheduleState.Disabled, slot.NextStartUtc, null);

            var lastAttempt = await _dbContext.JobImportRuns.AsNoTracking()
                .Where(r => r.Trigger == JobImportRunTrigger.DailySchedule)
                .OrderByDescending(r => r.StartedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var lastSuccess = await _dbContext.JobImportRuns.AsNoTracking()
                .Where(r => r.Trigger == JobImportRunTrigger.DailySchedule && r.IsSuccess)
                .OrderByDescending(r => r.StartedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var zone = JobImportDailySchedule.ResolveTimeZone(_schedule.TimeZone);
            var dto = new JobImportScheduleStatusDto
            {
                AutomaticImportEnabled = enabled,
                DailyRunTime = JobImportDailySchedule.ResolveRunTime(_schedule.DailyRunTime).ToString("HH:mm"),
                TimeZone = $"{zone.Id} (UTC{FormatOffset(zone.GetUtcOffset(slot.StartUtc))})",
                MaxRetryAttempts = JobImportDailySchedule.MaxRetries(_schedule),
                SlotDate = slot.Date.ToString("yyyy-MM-dd"),
                SlotStartUtc = slot.StartUtc,
                NextScheduledRunUtc = slot.NextStartUtc,
                State = evaluation.State,
                StateLabel = StateLabel(evaluation.State),
                AttemptsUsed = attempts.Count,
                NextRetryAtUtc = evaluation.State == JobImportScheduleState.RetryScheduled ? evaluation.NextCheckUtc : null,
                LastAttempt = lastAttempt is null ? null : JobImportService.MapRunToDto(lastAttempt),
                LastSuccessfulRun = lastSuccess is null ? null : JobImportService.MapRunToDto(lastSuccess),
                LastError = lastAttempt?.FailureMessage
            };

            return Result<JobImportScheduleStatusDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading the daily job import status");
            return Result<JobImportScheduleStatusDto>.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while loading the daily import status.");
        }
    }

    // ─── Claiming ─────────────────────────────────────────────────────

    /// <summary>
    /// Inserts the attempt's deterministic row. Returns null when another
    /// API instance already inserted it — that instance owns the attempt.
    /// Public so the cross-instance race is directly testable.
    /// </summary>
    public async Task<JobImportRun?> TryClaimAttemptAsync(JobImportSlot slot, int attemptNumber, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var run = new JobImportRun
        {
            Id = JobImportDailySchedule.AttemptId(slot.Date, attemptNumber),
            SourceId = null,
            SourceName = JobImportDailySchedule.BatchRunName,
            Trigger = JobImportRunTrigger.DailySchedule,
            Status = JobImportRunStatus.Running,
            StartedAtUtc = nowUtc,
            Notes = AttemptHeader(slot, attemptNumber)
        };

        _dbContext.JobImportRuns.Add(run);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return run;
        }
        catch (DbUpdateException)
        {
            _dbContext.JobImportRuns.Entry(run).State = EntityState.Detached;

            // Lost the race only if the row now exists; anything else is a
            // genuine database fault and must surface.
            if (!await _dbContext.JobImportRuns.AsNoTracking().AnyAsync(r => r.Id == run.Id, cancellationToken)) throw;

            _logger.LogInformation("[job-import][daily] attempt {Attempt} for {Slot} was claimed by another API instance; skipping.", attemptNumber, slot.Date);
            return null;
        }
    }

    // ─── One attempt ──────────────────────────────────────────────────

    private async Task ExecuteAttemptAsync(JobImportSlot slot, JobImportRun run, int attemptNumber, DateTime startedUtc, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = new AttemptOutcome();
        var notes = new List<string> { AttemptHeader(slot, attemptNumber) };
        string? attemptError = null;
        var interrupted = false;

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attemptCts.CancelAfter(JobImportDailySchedule.AttemptBudget);

        try
        {
            // Zombies first, so a run orphaned by a restart cannot make its
            // source look busy for the rest of the day.
            await _importService.ReapStuckRunsAsync(attemptCts.Token);

            var work = await ResolveWorkAsync(slot, DateTime.MaxValue, attemptCts.Token);

            if (attemptNumber == 1)
                _logger.LogInformation("[job-import][daily] starting the daily import for {Slot}: {Pending} source(s) due.", slot.Date, work.Pending.Count);
            else
            {
                _logger.LogWarning("[job-import][daily] retry {Retry} of {MaxRetries} for {Slot}: {Pending} source(s) to retry; {Done} already done.",
                    attemptNumber - 1, MaxAttempts - 1, slot.Date, work.Pending.Count, work.Succeeded.Count);
                notes.Add($"Retrying {work.Pending.Count} source(s); {work.Succeeded.Count} already refreshed earlier today are not crawled again.");
            }

            if (work.Pending.Count == 0) notes.Add("No scheduled sources were due.");

            foreach (var source in work.Pending)
            {
                if (attemptCts.IsCancellationRequested)
                {
                    outcome.NotReached.Add(source.Name);
                    continue;
                }

                if (await IsSourceBusyAsync(source.Id, attemptCts.Token))
                {
                    // A manual refresh (or another instance) is crawling it
                    // right now. Never run two crawls of one source at once.
                    outcome.Busy.Add(source.Name);
                    notes.Add($"{source.Name}: skipped — an import of this source was already running. It will be retried.");
                    continue;
                }

                using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token);
                sourceCts.CancelAfter(JobImportService.BackgroundRunLimit);

                try
                {
                    var result = await _importService.RunScheduledSourceAsync(source.Id, sourceCts.Token);
                    if (result is null)
                    {
                        notes.Add($"{source.Name}: skipped — no longer an active crawlable source.");
                    }
                    else
                    {
                        RecordSourceResult(source, result, outcome, notes);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The per-source or attempt time budget fired. The importer
                    // has already saved that source's run as failed with how
                    // far it got; it is retryable.
                    outcome.Retryable.Add(source.Name);
                    notes.Add($"{source.Name}: stopped by the time limit — will be retried.");
                    _logger.LogWarning("[job-import][daily] source {SourceId} ({SourceName}) hit its time limit during attempt {Attempt}.", source.Id, source.Name, attemptNumber);
                }

                await HeartbeatAsync(run, outcome, notes, stopwatch.Elapsed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            interrupted = true;
            _logger.LogWarning("[job-import][daily] attempt {Attempt} for {Slot} was interrupted by an API shutdown.", attemptNumber, slot.Date);
        }
        catch (Exception ex)
        {
            attemptError = JobImportDailySchedule.RedactSecrets($"{ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "[job-import][daily] attempt {Attempt} for {Slot} failed.", attemptNumber, slot.Date);
        }

        // The fetcher turns a cancelled request into a failed fetch rather
        // than an exception, so a shutdown can also end the loop quietly.
        interrupted |= stoppingToken.IsCancellationRequested;

        // The housekeeping every refresh does: anything past its closing
        // date stops being publicly listed.
        if (!interrupted)
        {
            try
            {
                var expired = await _jobService.ExpireClosedJobsAsync(CancellationToken.None);
                if (expired.IsSuccess) run.JobsExpired = expired.Data;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[job-import][daily] expiring closed jobs failed after attempt {Attempt}.", attemptNumber);
            }
        }

        stopwatch.Stop();
        // The real clock, not just start + elapsed: the per-source rows are
        // stamped with DateTime.UtcNow, and the next evaluation only reads
        // rows started up to this moment — every one of this attempt's
        // rows must fall inside it.
        var elapsedEnd = startedUtc + stopwatch.Elapsed;
        var realNow = DateTime.UtcNow;
        run.CompletedAtUtc = realNow > elapsedEnd ? realNow : elapsedEnd;
        run.DurationMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
        ApplyCounters(run, outcome);

        var unfinished = outcome.Retryable.Count + outcome.Permanent.Count + outcome.Busy.Count + outcome.NotReached.Count;
        run.Status = interrupted || (outcome.Succeeded == 0 && (unfinished > 0 || attemptError is not null))
            ? JobImportRunStatus.Failed
            : unfinished > 0 || attemptError is not null ? JobImportRunStatus.PartiallySucceeded : JobImportRunStatus.Succeeded;
        run.IsSuccess = run.Status != JobImportRunStatus.Failed;

        if (outcome.NotReached.Count > 0)
            notes.Add($"Stopped at the {JobImportDailySchedule.AttemptBudget.TotalHours:F0}h attempt limit before reaching: {string.Join(", ", outcome.NotReached)}.");

        run.FailureMessage = Truncate(interrupted
            ? "Interrupted — the API shut down during the import (normally an app-pool recycle or a deploy). Sources it finished were kept."
            : attemptError is not null ? $"The import stopped unexpectedly: {attemptError}" : null, MaxFailureLength);
        run.Notes = Truncate(string.Join('\n', notes), MaxNotesLength);

        await _dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private void RecordSourceResult(SourceWork source, JobImportRunDto result, AttemptOutcome outcome, List<string> notes)
    {
        outcome.Found += result.JobsFound;
        outcome.Created += result.JobsCreated;
        outcome.Updated += result.JobsUpdated;
        outcome.Skipped += result.JobsSkipped;

        if (result.IsSuccess)
        {
            outcome.Succeeded++;
            notes.Add($"{source.Name}: refreshed — {result.JobsCreated} new, {result.JobsUpdated} updated, {result.JobsSkipped} skipped.");
            return;
        }

        var message = JobImportDailySchedule.RedactSecrets(result.FailureMessage) ?? "no reason recorded";
        var kind = JobImportFailureClassifier.Classify(result.HttpStatusCode, result.FailureMessage);
        if (kind == JobImportFailureKind.Transient)
        {
            outcome.Retryable.Add(source.Name);
            notes.Add($"{source.Name}: failed, will be retried — {message}");
        }
        else
        {
            outcome.Permanent.Add(source.Name);
            notes.Add($"{source.Name}: failed, not retried today — {message}");
        }

        _logger.LogWarning("[job-import][daily] source {SourceId} ({SourceName}) failed ({Kind}, HTTP {Status}): {Message}",
            source.Id, source.Name, kind, result.HttpStatusCode, message);
    }

    // Progress counters double as the heartbeat: any modification stamps
    // UpdatedAtUtc, which is what tells another instance this attempt is
    // still alive.
    private async Task HeartbeatAsync(JobImportRun run, AttemptOutcome outcome, List<string> notes, TimeSpan elapsed)
    {
        ApplyCounters(run, outcome);
        run.Notes = Truncate(string.Join('\n', notes), MaxNotesLength);
        run.DurationMs = (int)Math.Min(int.MaxValue, elapsed.TotalMilliseconds);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private static void ApplyCounters(JobImportRun run, AttemptOutcome outcome)
    {
        run.JobsFound = outcome.Found;
        run.JobsCreated = outcome.Created;
        run.JobsUpdated = outcome.Updated;
        run.JobsSkipped = outcome.Skipped;
    }

    // Appends what happens next to the attempt row and the log, once the
    // slot has been re-evaluated with this attempt included.
    private async Task RecordOutcomeAsync(JobImportSlot slot, JobImportRun run, int attemptNumber, SlotEvaluation after)
    {
        var pending = after.Work?.Pending.Select(p => p.Name).ToList() ?? new List<string>();
        var permanent = after.Work?.PermanentFailures.Select(p => p.Name).ToList() ?? new List<string>();
        string? next = null;

        switch (after.State)
        {
            case JobImportScheduleState.RetryScheduled:
                next = $"Retry {attemptNumber} of {MaxAttempts - 1} scheduled for {JobImportDailySchedule.DescribeLocal(after.NextCheckUtc, _schedule)} "
                    + $"for {pending.Count} source(s): {string.Join(", ", pending)}.";
                _logger.LogWarning("[job-import][daily] {Slot} attempt {Attempt}: {Pending} source(s) still failing; retry {Retry} of {MaxRetries} at {RetryAtUtc:u}.",
                    slot.Date, attemptNumber, pending.Count, attemptNumber, MaxAttempts - 1, after.NextCheckUtc);
                break;

            case JobImportScheduleState.Failed:
                var reason = attemptNumber >= MaxAttempts
                    ? $"Retries exhausted after {attemptNumber} attempt(s)"
                    : "No time left for another retry before the next daily import";
                next = $"{reason}; {pending.Count} source(s) still failing: {string.Join(", ", pending)}. "
                    + $"The next daily import runs {JobImportDailySchedule.DescribeLocal(slot.NextStartUtc, _schedule)}.";
                _logger.LogError("[job-import][daily] {Slot} FAILED — {Reason}; {Pending} source(s) still failing: {Sources}. Next daily run at {NextRunUtc:u}.",
                    slot.Date, reason, pending.Count, string.Join(", ", pending), slot.NextStartUtc);
                break;

            case JobImportScheduleState.CompletedWithErrors:
                next = $"{permanent.Count} source(s) failed for a reason a retry cannot fix and will be tried again in tomorrow's run: {string.Join(", ", permanent)}.";
                _logger.LogWarning("[job-import][daily] {Slot} finished with {Permanent} permanently failing source(s): {Sources}.",
                    slot.Date, permanent.Count, string.Join(", ", permanent));
                break;

            default:
                _logger.LogInformation("[job-import][daily] {Slot} attempt {Attempt} finished: {Created} new, {Updated} updated, {Skipped} skipped, {Expired} expired in {DurationMs}ms.",
                    slot.Date, attemptNumber, run.JobsCreated, run.JobsUpdated, run.JobsSkipped, run.JobsExpired, run.DurationMs);
                break;
        }

        if (next is null) return;

        // A slot that ended Failed/RetryScheduled must say so on the row the
        // admin sees, even when this attempt itself partially succeeded.
        if (after.State == JobImportScheduleState.Failed)
        {
            run.Status = JobImportRunStatus.Failed;
            run.IsSuccess = false;
        }

        run.FailureMessage = Truncate(string.IsNullOrEmpty(run.FailureMessage) ? next : $"{run.FailureMessage} {next}", MaxFailureLength);
        run.Notes = Truncate($"{run.Notes}\n→ {next}", MaxNotesLength);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
    }

    // ─── Slot evaluation ──────────────────────────────────────────────

    private async Task<SlotEvaluation> EvaluateAsync(JobImportSlot slot, IReadOnlyList<Attempt> attempts, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (attempts.Any(a => a.Run.Status == JobImportRunStatus.Running))
            return new SlotEvaluation(JobImportScheduleState.Running, nowUtc + RunningRecheck, null);

        if (attempts.Count == 0)
            return new SlotEvaluation(JobImportScheduleState.Due, nowUtc, null);

        var last = attempts.MaxBy(a => a.Number)!.Run;
        var lastFinishedUtc = last.CompletedAtUtc ?? last.StartedAtUtc;

        // Only failures up to the last attempt decide retries. A source
        // that fails later on its own shorter interval does not reopen a
        // finished daily run.
        var work = await ResolveWorkAsync(slot, lastFinishedUtc, cancellationToken);
        if (work.Pending.Count == 0)
        {
            var state = work.PermanentFailures.Count == 0 ? JobImportScheduleState.Succeeded : JobImportScheduleState.CompletedWithErrors;
            return new SlotEvaluation(state, slot.NextStartUtc, work);
        }

        if (attempts.Count >= MaxAttempts)
            return new SlotEvaluation(JobImportScheduleState.Failed, slot.NextStartUtc, work);

        var retryAtUtc = lastFinishedUtc + JobImportDailySchedule.RetryDelay(attempts.Count, _schedule);

        // A retry never spills into the next day's slot; that one starts fresh.
        if (retryAtUtc >= slot.NextStartUtc)
            return new SlotEvaluation(JobImportScheduleState.Failed, slot.NextStartUtc, work);

        return nowUtc >= retryAtUtc
            ? new SlotEvaluation(JobImportScheduleState.Due, nowUtc, work)
            : new SlotEvaluation(JobImportScheduleState.RetryScheduled, retryAtUtc, work);
    }

    /// <summary>
    /// Sorts the slot's scheduled sources by what the per-source run rows
    /// (from the slot start up to <paramref name="upToUtc"/>) say happened
    /// to each one most recently.
    /// </summary>
    private async Task<SlotWork> ResolveWorkAsync(JobImportSlot slot, DateTime upToUtc, CancellationToken cancellationToken)
    {
        var sources = await _dbContext.JobSources.AsNoTracking()
            .Where(s => s.IsActive && s.SourceType != JobSourceType.Manual && s.CrawlFrequencyMinutes != null)
            .OrderBy(s => s.LastCheckedAtUtc ?? DateTime.MinValue)
            .Select(s => new { s.Id, s.SourceName, s.CrawlFrequencyMinutes, s.LastCheckedAtUtc })
            .ToListAsync(cancellationToken);

        var upper = upToUtc < slot.NextStartUtc ? upToUtc : slot.NextStartUtc;
        var runs = await _dbContext.JobImportRuns.AsNoTracking()
            .Where(r => r.SourceId != null && r.StartedAtUtc >= slot.StartUtc && r.StartedAtUtc <= upper)
            .Select(r => new { SourceId = r.SourceId!.Value, r.Status, r.IsSuccess, r.StartedAtUtc, r.HttpStatusCode, r.FailureMessage })
            .ToListAsync(cancellationToken);

        var latest = runs
            .GroupBy(r => r.SourceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAtUtc).First());

        var work = new SlotWork();
        foreach (var source in sources)
        {
            latest.TryGetValue(source.Id, out var last);
            if (last is null && !JobImportDailySchedule.IsInDailyScope(source.CrawlFrequencyMinutes!.Value, source.LastCheckedAtUtc, slot)) continue;

            var item = new SourceWork(source.Id, source.SourceName);
            if (last is null || last.Status == JobImportRunStatus.Running) work.Pending.Add(item);
            else if (last.IsSuccess) work.Succeeded.Add(item);
            else if (JobImportFailureClassifier.Classify(last.HttpStatusCode, last.FailureMessage) == JobImportFailureKind.Permanent) work.PermanentFailures.Add(item);
            else work.Pending.Add(item);
        }

        return work;
    }

    private async Task<List<Attempt>> LoadAttemptsAsync(JobImportSlot slot, bool tracked, CancellationToken cancellationToken)
    {
        var ids = Enumerable.Range(1, 1 + JobImportDailySchedule.MaxRetryAttemptsCeiling)
            .ToDictionary(n => JobImportDailySchedule.AttemptId(slot.Date, n), n => n);
        var keys = ids.Keys.ToList();

        var query = _dbContext.JobImportRuns.Where(r => keys.Contains(r.Id));
        if (!tracked) query = query.AsNoTracking();

        var rows = await query.ToListAsync(cancellationToken);
        return rows.Select(r => new Attempt(ids[r.Id], r)).OrderBy(a => a.Number).ToList();
    }

    // Marks attempts whose heartbeat stopped as interrupted. Returns false
    // only if one of them turned out to be alive after all (another
    // instance touched it while we were deciding).
    private async Task<bool> FailAbandonedAttemptsAsync(List<Attempt> attempts, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var abandoned = attempts
            .Where(a => a.Run.Status == JobImportRunStatus.Running && nowUtc - (a.Run.UpdatedAtUtc ?? a.Run.StartedAtUtc) >= JobImportDailySchedule.HeartbeatStaleAfter)
            .ToList();
        if (abandoned.Count == 0) return true;

        foreach (var (number, run) in abandoned)
        {
            var heartbeat = run.UpdatedAtUtc ?? run.StartedAtUtc;
            run.Status = JobImportRunStatus.Failed;
            run.IsSuccess = false;
            run.CompletedAtUtc = nowUtc;
            run.DurationMs = (int)Math.Min(int.MaxValue, (heartbeat - run.StartedAtUtc).TotalMilliseconds);
            run.FailureMessage = Truncate(
                $"Attempt {number} stopped reporting progress at {heartbeat:yyyy-MM-dd HH:mm} UTC and was marked interrupted — the API most likely restarted mid-import. "
                + "Sources it finished were kept; the rest are retried.", MaxFailureLength);
            _logger.LogWarning("[job-import][daily] attempt {Attempt} (run {RunId}) had no heartbeat since {HeartbeatUtc:u}; marked interrupted.", number, run.Id, heartbeat);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            foreach (var (_, run) in abandoned) await _dbContext.JobImportRuns.Entry(run).ReloadAsync(cancellationToken);
            return false;
        }
    }

    private Task<bool> IsSourceBusyAsync(Guid sourceId, CancellationToken cancellationToken)
        => _dbContext.JobImportRuns.AsNoTracking().AnyAsync(r => r.SourceId == sourceId && r.Status == JobImportRunStatus.Running, cancellationToken);

    private string AttemptHeader(JobImportSlot slot, int attemptNumber)
        => $"Attempt {attemptNumber} of {MaxAttempts} for the daily import scheduled {JobImportDailySchedule.DescribeLocal(slot.StartUtc, _schedule)}.";

    private static string StateLabel(JobImportScheduleState state) => state switch
    {
        JobImportScheduleState.Disabled => "Automatic import is switched off",
        JobImportScheduleState.Due => "Due — starts on the next worker check",
        JobImportScheduleState.Running => "Running",
        JobImportScheduleState.RetryScheduled => "Retry scheduled",
        JobImportScheduleState.Succeeded => "Succeeded",
        JobImportScheduleState.CompletedWithErrors => "Completed with source errors",
        JobImportScheduleState.Failed => "Failed — retries exhausted",
        _ => state.ToString()
    };

    private static string FormatOffset(TimeSpan offset) => $"{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed record Attempt(int Number, JobImportRun Run);

    private sealed record SourceWork(Guid Id, string Name);

    private sealed class SlotWork
    {
        public List<SourceWork> Pending { get; } = new();
        public List<SourceWork> Succeeded { get; } = new();
        public List<SourceWork> PermanentFailures { get; } = new();
    }

    private sealed record SlotEvaluation(JobImportScheduleState State, DateTime NextCheckUtc, SlotWork? Work);

    private sealed class AttemptOutcome
    {
        public int Succeeded { get; set; }
        public int Found { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public List<string> Retryable { get; } = new();
        public List<string> Permanent { get; } = new();
        public List<string> Busy { get; } = new();
        public List<string> NotReached { get; } = new();
    }
}

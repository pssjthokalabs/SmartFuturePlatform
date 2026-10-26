using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Jobs.Import;

public enum JobImportScheduleState
{
    // JobsModuleEnabled or AutoImportEnabled is off — nothing is scheduled.
    Disabled = 0,
    // An attempt should start now: the first of the day, or a retry whose
    // backoff has elapsed. Seen by the admin only while the API is idle.
    Due = 1,
    // An attempt is in progress, on this instance or another one.
    Running = 2,
    // The last attempt left sources worth retrying; the next attempt waits
    // for its backoff.
    RetryScheduled = 3,
    // Every source in today's run succeeded (or none was due).
    Succeeded = 4,
    // Finished, but some sources failed for a reason a retry cannot fix
    // (blocked, not found, unparseable). Tomorrow's run tries them again.
    CompletedWithErrors = 5,
    // Retries exhausted with sources still failing.
    Failed = 6
}

/// <param name="State">The slot's state after this tick.</param>
/// <param name="SlotDate">Local date of the slot that was evaluated.</param>
/// <param name="AttemptsUsed">Attempts recorded for the slot so far.</param>
/// <param name="AttemptExecuted">The attempt this tick ran, if any.</param>
/// <param name="NextCheckUtc">When the worker should look again.</param>
public sealed record JobImportScheduleTickResult(JobImportScheduleState State, DateOnly SlotDate, int AttemptsUsed, int? AttemptExecuted, DateTime NextCheckUtc);

/// <summary>
/// The once-daily scheduled job import. Called by JobImportHostedService
/// on every tick; decides from the database alone whether today's import
/// (or a retry of it) is due, claims it so no other API instance can run
/// it too, and runs it. Safe to call as often as you like — a slot that
/// is finished, running elsewhere, or waiting on backoff is a no-op.
/// </summary>
public interface IJobImportScheduler
{
    Task<JobImportScheduleTickResult> RunDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    // Read-only — never claims or runs anything.
    Task<Result<JobImportScheduleStatusDto>> GetStatusAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}

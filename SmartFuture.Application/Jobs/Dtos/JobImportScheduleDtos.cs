using SmartFuture.Application.Jobs.Import;

namespace SmartFuture.Application.Jobs.Dtos;

// GET /api/admin/jobs/import-schedule — the admin's answer to "did the
// daily import run, and if not, why?" without opening the run log.
public class JobImportScheduleStatusDto
{
    // JobsModuleEnabled && AutoImportEnabled. False = nothing runs on a
    // schedule; manual refresh still works.
    public bool AutomaticImportEnabled { get; set; }

    public string DailyRunTime { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public int MaxRetryAttempts { get; set; }

    // The slot being reported on: today's run, or yesterday's if it is
    // still before today's scheduled time.
    public string SlotDate { get; set; } = string.Empty;
    public DateTime SlotStartUtc { get; set; }
    public DateTime NextScheduledRunUtc { get; set; }

    public JobImportScheduleState State { get; set; }
    public string StateLabel { get; set; } = string.Empty;
    public int AttemptsUsed { get; set; }
    public DateTime? NextRetryAtUtc { get; set; }

    public JobImportRunDto? LastAttempt { get; set; }
    public JobImportRunDto? LastSuccessfulRun { get; set; }
    public string? LastError { get; set; }
}

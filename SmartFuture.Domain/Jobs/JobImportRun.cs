using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Domain.Jobs;

// One import attempt against one source. A run row is written even when
// the fetch fails outright — a source that blocks crawling must show up
// in the admin log rather than disappearing silently.
public class JobImportRun : BaseEntity
{
    public Guid? SourceId { get; set; }
    public JobSource? Source { get; set; }
    // Denormalised so the log still reads correctly after a source is
    // renamed or deleted.
    public string SourceName { get; set; } = string.Empty;

    public JobImportRunStatus Status { get; set; } = JobImportRunStatus.Running;
    public JobImportRunTrigger Trigger { get; set; } = JobImportRunTrigger.Manual;
    public Guid? TriggeredByUserId { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public int DurationMs { get; set; }

    public int JobsFound { get; set; }
    public int JobsCreated { get; set; }
    public int JobsUpdated { get; set; }
    public int JobsSkipped { get; set; }
    public int JobsExpired { get; set; }

    public bool IsSuccess { get; set; }
    public string? FailureMessage { get; set; }
    // Human-readable per-item notes ("skipped: no title", "blocked: 403").
    // Capped by the importer so a pathological page can't write a novel.
    public string? Notes { get; set; }
    public int? HttpStatusCode { get; set; }
}

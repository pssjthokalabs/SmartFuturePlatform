using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Dtos;

public class JobImportRunDto
{
    public Guid Id { get; set; }
    public Guid? SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;

    public JobImportRunStatus Status { get; set; }
    public string StatusLabel { get; set; } = string.Empty;
    public JobImportRunTrigger Trigger { get; set; }
    public string TriggerLabel { get; set; } = string.Empty;
    public Guid? TriggeredByUserId { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int DurationMs { get; set; }

    public int JobsFound { get; set; }
    public int JobsCreated { get; set; }
    public int JobsUpdated { get; set; }
    public int JobsSkipped { get; set; }
    public int JobsExpired { get; set; }

    public bool IsSuccess { get; set; }
    public string? FailureMessage { get; set; }
    public string? Notes { get; set; }
    public int? HttpStatusCode { get; set; }
}

public class JobImportRunFilterRequestDto
{
    public Guid? SourceId { get; set; }
    public JobImportRunStatus? Status { get; set; }
    public bool? IsSuccess { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

// Returned by the manual refresh endpoints. Aggregates the per-source
// runs so the admin sees one summary line for a "refresh all".
public class JobImportSummaryDto
{
    public int SourcesAttempted { get; set; }
    public int SourcesSucceeded { get; set; }
    public int SourcesFailed { get; set; }
    public int JobsCreated { get; set; }
    public int JobsUpdated { get; set; }
    public int JobsSkipped { get; set; }
    public int JobsExpired { get; set; }
    public IReadOnlyList<JobImportRunDto> Runs { get; set; } = Array.Empty<JobImportRunDto>();
}

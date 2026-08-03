using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Dtos;

public class JobSourceDto
{
    public Guid Id { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public JobSourceType SourceType { get; set; }
    public string SourceTypeLabel { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public string? Notes { get; set; }
    public string? DefaultCategory { get; set; }
    public string? DefaultLocation { get; set; }
    public int? CrawlFrequencyMinutes { get; set; }
    public bool AutoPublish { get; set; }
    public int MaxJobsPerRun { get; set; }
    public int MaxPagesPerRun { get; set; }

    public DateTime? LastCheckedAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public DateTime? LastFailureAtUtc { get; set; }
    public string? LastFailureMessage { get; set; }
    public int ConsecutiveFailureCount { get; set; }
    public int TotalJobsImported { get; set; }

    // Live counts so the admin list doesn't need a second call.
    public int ActiveJobCount { get; set; }
    public int TotalJobCount { get; set; }

    // Convenience flag for the status pill: healthy, never-run, or
    // failing. Computed, never stored.
    public string HealthLabel { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class JobSourceFilterRequestDto
{
    public string? Search { get; set; }
    public JobSourceType? SourceType { get; set; }
    public bool? IsActive { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

public class CreateJobSourceRequestDto
{
    public string SourceName { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public JobSourceType SourceType { get; set; } = JobSourceType.HtmlPage;
    public bool? IsActive { get; set; }
    public string? Notes { get; set; }
    public string? DefaultCategory { get; set; }
    public string? DefaultLocation { get; set; }
    public int? CrawlFrequencyMinutes { get; set; }
    public bool? AutoPublish { get; set; }
    public int? MaxJobsPerRun { get; set; }
    public int? MaxPagesPerRun { get; set; }
}

public class UpdateJobSourceRequestDto
{
    public string? SourceName { get; set; }
    public string? SourceUrl { get; set; }
    public JobSourceType? SourceType { get; set; }
    public bool? IsActive { get; set; }
    public string? Notes { get; set; }
    public string? DefaultCategory { get; set; }
    public string? DefaultLocation { get; set; }
    public int? CrawlFrequencyMinutes { get; set; }
    public bool? AutoPublish { get; set; }
    public int? MaxJobsPerRun { get; set; }
    public int? MaxPagesPerRun { get; set; }
}

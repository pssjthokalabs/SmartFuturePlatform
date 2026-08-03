namespace SmartFuture.Application.Jobs.Import;

// Outcome of one HTTP fetch. Never throws to the caller — a blocked or
// unreachable source is DATA (recorded on the run + the source's health
// fields), not an exception that aborts the whole import.
public class JobFetchResult
{
    public bool IsSuccess { get; set; }
    public int? StatusCode { get; set; }
    public string? Content { get; set; }
    public string? ContentType { get; set; }
    public string? FailureMessage { get; set; }
    public string? FinalUrl { get; set; }

    public static JobFetchResult Failure(string message, int? statusCode = null) => new()
    {
        IsSuccess = false,
        FailureMessage = message,
        StatusCode = statusCode
    };
}

public interface IJobSourceFetcher
{
    Task<JobFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default);
}

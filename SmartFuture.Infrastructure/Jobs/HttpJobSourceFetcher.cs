using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Jobs.Import;

namespace SmartFuture.Infrastructure.Jobs;

// The only place the job importer touches the public internet.
//
// Design rules:
//   • Identify honestly. A real User-Agent naming Smart Future and a
//     contact URL is what a site operator needs in order to allow (or
//     deliberately block) us. No browser-impersonation, no rotation.
//   • Never throw. A 403/429/timeout is a normal outcome for a public
//     job board and must land in the run log, not in an unhandled
//     exception that kills the rest of the run.
//   • Cap the response size so one enormous page can't exhaust memory.
public class HttpJobSourceFetcher : IJobSourceFetcher
{
    public const string HttpClientName = "JobSourceFetcher";

    // 5 MB of markup is far beyond any legitimate listings page.
    private const int MaxContentBytes = 5 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpJobSourceFetcher> _logger;

    public HttpJobSourceFetcher(HttpClient httpClient, ILogger<HttpJobSourceFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<JobFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return JobFetchResult.Failure("Source URL is empty.");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return JobFetchResult.Failure("Source URL must be a valid http(s) address.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml", 0.9));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                // Translate the common blocking codes into something an
                // admin can act on from the Sources page.
                var reason = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => "The source refused the request (403). This site likely blocks automated access.",
                    HttpStatusCode.NotFound => "The source URL returned 404. Check that the address is still correct.",
                    HttpStatusCode.TooManyRequests => "The source rate-limited us (429). Increase the crawl interval for this source.",
                    HttpStatusCode.Unauthorized => "The source requires authentication (401).",
                    _ => $"The source returned HTTP {statusCode} ({response.ReasonPhrase})."
                };

                return JobFetchResult.Failure(reason, statusCode);
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > MaxContentBytes)
            {
                return JobFetchResult.Failure(
                    $"The source page is too large to process ({declaredLength} bytes; limit {MaxContentBytes}).", statusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var content = await ReadCappedAsync(stream, cancellationToken);

            return new JobFetchResult
            {
                IsSuccess = true,
                StatusCode = statusCode,
                Content = content,
                ContentType = response.Content.Headers.ContentType?.MediaType,
                FinalUrl = response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString()
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as TaskCanceledException.
            _logger.LogWarning("Job source fetch timed out for {Url}", url);
            return JobFetchResult.Failure("The source timed out. It may be slow or blocking automated requests.");
        }
        catch (OperationCanceledException)
        {
            return JobFetchResult.Failure("The import run was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Job source fetch failed for {Url}", url);
            return JobFetchResult.Failure($"Could not reach the source: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching job source {Url}", url);
            return JobFetchResult.Failure("An unexpected error occurred while fetching the source.");
        }
    }

    // Read at most MaxContentBytes even when the server lied about (or
    // omitted) Content-Length.
    private static async Task<string> ReadCappedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var remaining = MaxContentBytes - (int)limited.Length;
            if (remaining <= 0) break;

            limited.Write(buffer, 0, Math.Min(read, remaining));
            if (limited.Length >= MaxContentBytes) break;
        }

        limited.Position = 0;
        using var reader = new StreamReader(limited, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}

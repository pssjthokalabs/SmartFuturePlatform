using System.Threading.Channels;

namespace SmartFuture.Application.Jobs.Import;

/// <summary>
/// One queued manual refresh: the source to crawl and the
/// already-persisted <see cref="Domain.Jobs.JobImportRun"/> row that is
/// waiting in <c>Running</c> state for the worker to fill in.
/// </summary>
/// <param name="SourceId">Source to crawl.</param>
/// <param name="RunId">Run row created by the endpoint before queueing.</param>
public sealed record JobImportQueueItem(Guid SourceId, Guid RunId);

/// <summary>
/// Hand-off between the admin refresh endpoint and
/// <c>JobImportHostedService</c>.
///
/// WHY THIS EXISTS
///
/// A refresh crawls one page per Max Pages plus one page per job,
/// sequentially, each fetch up to 20s. Running that inside the HTTP
/// request meant the request could be killed by any of five layers that
/// have nothing to do with us — Cloudflare, IIS/ANCM, the browser, an
/// app-pool recycle, or simply a slow job board. Every one of those
/// looks like a different bug to the admin (the Cloudflare cut produced
/// a *bogus* CORS error, because a killed connection writes no CORS
/// header). Raising the timeout just moves which layer kills it.
///
/// So the crawl no longer lives in the request. The endpoint records a
/// run, drops it here, and returns 202 immediately; the hosted service
/// drains this queue on its own time.
///
/// IN-PROCESS ON PURPOSE: a Channel, not a durable broker. The
/// deployment is a single IIS app pool, and the durability that matters
/// — the run row — is already in SQL. If the process dies mid-crawl the
/// queue is lost, and that is exactly the case the stuck-run reaper in
/// JobImportService cleans up. A real broker would be a much larger
/// change for a failure mode the reaper already covers.
/// </summary>
public interface IJobImportQueue
{
    /// <summary>
    /// Queue a run. Returns false only if the channel is full, which
    /// the caller surfaces rather than blocking the request thread.
    /// </summary>
    bool TryEnqueue(JobImportQueueItem item);

    /// <summary>Drained by the hosted service; completes only on shutdown.</summary>
    IAsyncEnumerable<JobImportQueueItem> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>Items waiting. Surfaced to the admin as queue position.</summary>
    int PendingCount { get; }
}

/// <inheritdoc />
public sealed class JobImportQueue : IJobImportQueue
{
    // Bounded so a runaway caller cannot grow the queue without limit.
    // 100 is far beyond any real admin's click rate — duplicate-run
    // protection in JobImportService means one source can only ever hold
    // one slot at a time, so this is really a cap on distinct sources.
    private const int Capacity = 100;

    private readonly Channel<JobImportQueueItem> _channel =
        Channel.CreateBounded<JobImportQueueItem>(new BoundedChannelOptions(Capacity)
        {
            // Reject rather than block: TryEnqueue is called on a request
            // thread and must never wait.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    private int _pending;

    public int PendingCount => Volatile.Read(ref _pending);

    public bool TryEnqueue(JobImportQueueItem item)
    {
        if (!_channel.Writer.TryWrite(item)) return false;
        Interlocked.Increment(ref _pending);
        return true;
    }

    public async IAsyncEnumerable<JobImportQueueItem> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _pending);
            yield return item;
        }
    }
}

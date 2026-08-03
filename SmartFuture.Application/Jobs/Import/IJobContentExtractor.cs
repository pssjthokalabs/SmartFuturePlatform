using SmartFuture.Domain.Jobs;

namespace SmartFuture.Application.Jobs.Import;

// Turns fetched bytes into candidate jobs. Pure and synchronous — no
// network, no database — so extraction behaviour is unit-testable
// against saved fixtures.
public interface IJobContentExtractor
{
    JobExtractionResult Extract(JobSource source, string content, string? contentType, DateTime nowUtc);
}

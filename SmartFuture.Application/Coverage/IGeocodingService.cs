using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage;

// Address → lat/lon resolution. Failure modes are explicit:
//   PROVIDER_NOT_CONFIGURED  — no API key on file (caller must
//                              supply lat/lon directly)
//   NOT_FOUND                — provider responded but no match
//   EXCEPTION                — network / unparseable / timeout
public interface IGeocodingService
{
    Task<Result<GeocodeResult>> GeocodeAsync(string addressText, CancellationToken cancellationToken = default);
}

public record GeocodeResult(
    decimal Latitude,
    decimal Longitude,
    string? FormattedAddress);

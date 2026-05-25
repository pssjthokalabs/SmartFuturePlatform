using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage.Providers;

// Adapter over an external fibre-coverage lookup (Openserve today;
// could be Vumatel/Frogfoot later). Returns a normalised DTO ready
// for the controller — no upstream payload leaks out.
public interface IFibreCoverageProvider
{
    Task<Result<CoverageCheckResponseDto>> CheckAsync(
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken = default);
}

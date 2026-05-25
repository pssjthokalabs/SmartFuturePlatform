using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage;

public interface ICoverageCheckService
{
    Task<Result<CoverageCheckResponseDto>> CheckAsync(
        CoverageCheckRequestDto request,
        CancellationToken cancellationToken = default);
}

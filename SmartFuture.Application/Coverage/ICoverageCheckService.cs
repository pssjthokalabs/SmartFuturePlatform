using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage;

public interface ICoverageCheckService
{
    Task<Result<CoverageCheckResponseDto>> CheckAsync(
        CoverageCheckRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The customer chooses (and confirms) one of the Openserve service locations a coverage check listed
    /// (only while the Openserve integration is the Fibre authority). Answers like a coverage check for that
    /// location; never changes the customer's installation address.
    /// </summary>
    Task<Result<CoverageCheckResponseDto>> SelectServicePremisesAsync(
        CoverageServicePremisesSelectionRequestDto request,
        CancellationToken cancellationToken = default);
}

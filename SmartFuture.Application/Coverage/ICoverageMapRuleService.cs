using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage;

public interface ICoverageMapRuleService
{
    // Admin surfaces (called from AdminCoverageMapController).
    Task<Result<IReadOnlyList<CoverageMapRuleDto>>> GetAllAsync(CancellationToken ct = default);
    Task<Result<CoverageMapRuleDto>> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Result<CoverageMapRuleDto>> CreateAsync(UpsertCoverageMapRuleRequestDto request, CancellationToken ct = default);
    Task<Result<CoverageMapRuleDto>> UpdateAsync(Guid id, UpsertCoverageMapRuleRequestDto request, CancellationToken ct = default);
    Task<Result<CoverageMapRuleDto>> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default);

    // Runtime — called by CoverageCheckService BEFORE Openserve.
    // Excludes evaluated first (Excludes win over Includes at any
    // priority). Matched=false means "no active rule tripped; caller
    // should continue with Openserve."
    Task<CoverageMapEvaluationResult> TryEvaluateAsync(
        CoverageCheckRequestDto request,
        CancellationToken ct = default);
}

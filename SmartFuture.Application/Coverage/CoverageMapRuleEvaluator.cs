using Microsoft.EntityFrameworkCore;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Coverage;

namespace SmartFuture.Application.Coverage;

// Loads the ACTIVE Coverage Map rules and applies them in the one
// documented order — Excludes first (a narrow excluded suburb inside a
// broadly-included city has to win), then Includes — through
// CoverageMapRuleMatcher. Shared by the public coverage check
// (CoverageMapRuleService) and the checkout routing (FibreFulfilmentRouting)
// so SmartFuture's inclusion list is read identically in both places.
public static class CoverageMapRuleEvaluator
{
    public static async Task<CoverageMapEvaluationResult> EvaluateAsync(IAppDbContext dbContext, CoverageCheckRequestDto? request, CancellationToken cancellationToken = default)
    {
        if (request is null) return new CoverageMapEvaluationResult { Matched = false };

        var excludes = await dbContext.CoverageMapRules.AsNoTracking()
            .Where(r => r.IsActive && r.RuleType == CoverageMapRuleType.Exclude)
            .OrderBy(r => r.Priority).ThenBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var excludeHit = CoverageMapRuleMatcher.FindFirstMatch(excludes, request);
        if (excludeHit is not null) return excludeHit;

        var includes = await dbContext.CoverageMapRules.AsNoTracking()
            .Where(r => r.IsActive && r.RuleType == CoverageMapRuleType.Include)
            .OrderBy(r => r.Priority).ThenBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return CoverageMapRuleMatcher.FindFirstMatch(includes, request) ?? new CoverageMapEvaluationResult { Matched = false };
    }

    /// <summary>True when an ACTIVE Include rule matched and no Exclude rule did.</summary>
    public static bool IsInclusion(CoverageMapEvaluationResult? result) => result is { Matched: true, MatchedType: CoverageMapRuleType.Include };
}

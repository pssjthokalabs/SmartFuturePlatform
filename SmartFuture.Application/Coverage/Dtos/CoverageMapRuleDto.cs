using SmartFuture.Shared.Enums.Coverage;

namespace SmartFuture.Application.Coverage.Dtos;

public class CoverageMapRuleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MatchText { get; set; } = string.Empty;
    public CoverageMapRuleType RuleType { get; set; }
    public CoverageMatchMode MatchMode { get; set; }
    public CoverageAddressMatchComponent AllowedComponents { get; set; }
    public bool IsActive { get; set; }
    public int Priority { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class UpsertCoverageMapRuleRequestDto
{
    public string? Name { get; set; }
    public string? MatchText { get; set; }
    public CoverageMapRuleType RuleType { get; set; } = CoverageMapRuleType.Include;
    public CoverageMatchMode MatchMode { get; set; } = CoverageMatchMode.Contains;

    // Optional — service applies SafeAreaDefault when the caller sends
    // null / None so new rules never accidentally ship with permissive
    // full-address matching.
    public CoverageAddressMatchComponent? AllowedComponents { get; set; }

    public bool IsActive { get; set; } = true;
    public int Priority { get; set; }
    public string? Notes { get; set; }
}

// Result of consulting the rules table for a single coverage check.
// The service produces this; the CoverageCheckService orchestrator
// short-circuits its response on a Matched=true outcome.
public class CoverageMapEvaluationResult
{
    public bool Matched { get; set; }
    public CoverageMapRuleType MatchedType { get; set; }
    public Guid? MatchedRuleId { get; set; }
    public string? MatchedRuleName { get; set; }
    public CoverageAddressMatchComponent MatchedComponent { get; set; }
    public string? MatchedValue { get; set; }
}

// Admin "would this address match?" probe. Reuses the same evaluator
// the coverage endpoint uses.
public class CoverageMapTestMatchRequestDto
{
    public CoverageCheckRequestDto Address { get; set; } = new();
}

public class CoverageMapTestMatchResponseDto
{
    public CoverageMapEvaluationResult Evaluation { get; set; } = new();
    public string? SuggestedOutcome { get; set; }
}

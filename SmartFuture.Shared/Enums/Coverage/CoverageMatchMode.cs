namespace SmartFuture.Shared.Enums.Coverage;

// How the rule's MatchText is compared against an incoming address
// component. Comparison is always case-insensitive, whitespace-
// collapsed and trimmed on both sides — see
// CoverageMapRuleService.NormalizeForMatch.
public enum CoverageMatchMode
{
    Exact      = 1,
    Contains   = 2,
    StartsWith = 3
}

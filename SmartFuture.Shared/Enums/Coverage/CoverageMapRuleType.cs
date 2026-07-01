namespace SmartFuture.Shared.Enums.Coverage;

// Include = admin says "this area IS covered, don't ask Openserve".
// Exclude = admin says "this area is NOT covered, don't ask Openserve".
// Evaluation order: Excludes are checked FIRST — a narrow excluded
// suburb inside a broadly-included city still wins.
public enum CoverageMapRuleType
{
    Include = 1,
    Exclude = 2
}

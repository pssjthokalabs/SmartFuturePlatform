using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.Coverage;

namespace SmartFuture.Domain.Coverage;

// Admin-configured coverage override. Consulted by CoverageCheckService
// BEFORE the Openserve provider — if any active rule matches the
// incoming address, the coverage check short-circuits with an
// admin-authored answer and Openserve is not called.
//
// Match semantics (see CoverageMapRuleService):
//   • Excludes are evaluated first (evaluation order:
//     `Priority ASC, CreatedAtUtc ASC` within each rule-type bucket).
//   • Only the components ticked in AllowedComponents are considered —
//     so a rule with MatchText="Centurion" and only Suburb+City+Town
//     ticked will NOT be tripped by "12 Centurion Street".
//   • Comparison is case-insensitive, whitespace-collapsed, invariant
//     culture. See NormalizeForMatch in the service.
public class CoverageMapRule : BaseEntity
{
    // Admin-facing label — free text, displayed in the list. Not
    // matched against anything.
    public string Name { get; set; } = string.Empty;

    // The literal to match — e.g. "Centurion" or "Giyani". Normalised
    // at match time; stored verbatim so admins can see what they typed.
    public string MatchText { get; set; } = string.Empty;

    public CoverageMapRuleType RuleType { get; set; }
    public CoverageMatchMode MatchMode { get; set; } = CoverageMatchMode.Contains;

    // Bitmask of components the rule is allowed to look at. Persisted
    // as int (see EF config); the service reads it via HasFlag().
    public CoverageAddressMatchComponent AllowedComponents { get; set; }
        = CoverageAddressMatchComponent.SafeAreaDefault;

    // Disabled rows still exist for audit but never affect a coverage
    // check. Admin can flip this from the UI's toggle.
    public bool IsActive { get; set; } = true;

    // Lower value = evaluated earlier. Tie-break by CreatedAtUtc.
    public int Priority { get; set; }

    // Optional admin-only notes ("Why does this rule exist?"). Never
    // surfaced to the public coverage response.
    public string? Notes { get; set; }
}

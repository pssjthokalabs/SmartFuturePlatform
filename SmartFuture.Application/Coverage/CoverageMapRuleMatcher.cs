using System.Text.RegularExpressions;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Domain.Coverage;
using SmartFuture.Shared.Enums.Coverage;

namespace SmartFuture.Application.Coverage;

// Pure static matching core for the admin Coverage Map. Owns three
// things and NOTHING else:
//   1. Normalise both the rule's MatchText and the request's component
//      values the same way (trim → collapse whitespace → lowercase-invariant).
//   2. Walk a rule set in priority order and return the first rule that
//      matches an ALLOWED component on the request.
//   3. Apply Exact / Contains / StartsWith semantics against the
//      normalised values.
//
// This mirrors the decision-core pattern established in Phase 3/5
// (PaymentSettlementCore, CoverageMapRuleService's runtime wrapper).
// A pure static core means:
//   • CoverageMapRuleService keeps ONE responsibility — load rules from
//     EF, delegate matching to the core, log the decision.
//   • Unit tests exercise every branch without a DB, provider, or DI.
//   • The admin "test-match" endpoint and the public coverage endpoint
//     both share the same matcher — behaviour cannot drift between them.
//
// The matcher deliberately does NOT read the rule's IsActive flag or
// bucket rules by Exclude/Include priority — that's the caller's job.
// Callers pass in one already-filtered, already-sorted list per pass
// (Exclude first, then Include; see CoverageMapRuleService.TryEvaluateAsync).
public static class CoverageMapRuleMatcher
{
    // Cheap whitespace collapser used on both the rule.MatchText and the
    // request component. Runs O(n) with no allocations beyond the final
    // string.
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    // Walk `rules` in the order the caller passed them and return the
    // first (rule, component) pair that matches. Null when no rule
    // tripped. Behaviour on empty inputs is a no-match — never a throw.
    public static CoverageMapEvaluationResult? FindFirstMatch(
        IReadOnlyList<CoverageMapRule> rules,
        CoverageCheckRequestDto request)
    {
        if (rules is null || rules.Count == 0) return null;
        if (request is null) return null;

        // Pre-build the (component, value) pairs the request carries so
        // we walk each rule at O(components) instead of re-hashing.
        var candidates = BuildCandidates(request);
        if (candidates.Count == 0) return null;

        foreach (var rule in rules)
        {
            var normText = NormalizeForMatch(rule.MatchText);
            if (string.IsNullOrEmpty(normText)) continue;

            foreach (var (component, rawValue) in candidates)
            {
                if (!rule.AllowedComponents.HasFlag(component)) continue;
                var normValue = NormalizeForMatch(rawValue);
                if (string.IsNullOrEmpty(normValue)) continue;
                if (!IsMatch(normValue, normText, rule.MatchMode)) continue;

                return new CoverageMapEvaluationResult
                {
                    Matched = true,
                    MatchedType = rule.RuleType,
                    MatchedRuleId = rule.Id,
                    MatchedRuleName = rule.Name,
                    MatchedComponent = component,
                    MatchedValue = rawValue,
                };
            }
        }

        return null;
    }

    // Compare an already-normalised value against an already-normalised
    // needle. `StringComparison.Ordinal` is safe here because both sides
    // are lowercase-invariant.
    public static bool IsMatch(string haystack, string needle, CoverageMatchMode mode)
        => mode switch
        {
            CoverageMatchMode.Exact      => string.Equals(haystack, needle, StringComparison.Ordinal),
            CoverageMatchMode.StartsWith => haystack.StartsWith(needle, StringComparison.Ordinal),
            _                            => haystack.Contains(needle, StringComparison.Ordinal),
        };

    // trim → collapse whitespace → lowercase (invariant). This is the
    // ONE place normalisation happens; rule authoring in the admin UI
    // should preview against the same string. Public so admin tooling
    // (Coverage Map preview) can render the exact match target.
    public static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        var collapsed = WhitespaceRegex.Replace(trimmed, " ");
        return collapsed.ToLowerInvariant();
    }

    // Build the (component, raw value) pairs a request carries. Kept in
    // the same declaration order as the CoverageAddressMatchComponent
    // flags so a rule that ticks multiple components picks a
    // deterministic winner (FullAddress before AddressLine1 before ...).
    private static List<(CoverageAddressMatchComponent Component, string RawValue)> BuildCandidates(
        CoverageCheckRequestDto req)
    {
        var list = new List<(CoverageAddressMatchComponent, string)>(12);
        Add(list, CoverageAddressMatchComponent.FullAddress,      req.AddressText);
        Add(list, CoverageAddressMatchComponent.AddressLine1,     req.AddressLine1);
        Add(list, CoverageAddressMatchComponent.AddressLine2,     req.AddressLine2);
        Add(list, CoverageAddressMatchComponent.StreetName,       req.StreetName);
        Add(list, CoverageAddressMatchComponent.Suburb,           req.Suburb);
        Add(list, CoverageAddressMatchComponent.City,             req.City);
        Add(list, CoverageAddressMatchComponent.Town,             req.Town);
        Add(list, CoverageAddressMatchComponent.Province,         req.Province);
        Add(list, CoverageAddressMatchComponent.PostalCode,       req.PostalCode);
        Add(list, CoverageAddressMatchComponent.Country,          req.Country);
        Add(list, CoverageAddressMatchComponent.FormattedAddress, req.FormattedAddress);
        Add(list, CoverageAddressMatchComponent.PlaceName,        req.PlaceName);
        return list;
    }

    private static void Add(
        List<(CoverageAddressMatchComponent, string)> list,
        CoverageAddressMatchComponent component, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) list.Add((component, value));
    }
}

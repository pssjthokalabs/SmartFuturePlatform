using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Domain.Coverage;
using SmartFuture.Shared.Enums.Coverage;

namespace SmartFuture.Tests.Coverage;

// Phase-8 tests for the extracted CoverageMapRuleMatcher. Pure static
// core — no fixtures, no DB, no HTTP. Locks the safe-area matching
// contract that the admin Coverage Map depends on:
//
//   • A rule that only ticks Suburb/City/Town/Province/PostalCode/
//     Country MUST match against those structured components.
//   • The same rule MUST NOT trip on a coincidental street name or
//     formatted address that happens to contain the match text.
//   • Exclude rules win over Include rules (the caller enforces the
//     order, this test locks it too).
//   • Contains / Exact / StartsWith modes behave predictably against
//     trimmed + whitespace-collapsed + case-insensitive input.
public class CoverageMapRuleMatcherTests
{
    // Convenient rule factory — defaults to the Safe Area preset the
    // admin form pre-ticks. Every test opts-in to the risky flags
    // explicitly so we can never accidentally test a rule that
    // silently allowed StreetName / FormattedAddress.
    private static CoverageMapRule Rule(
        string matchText,
        CoverageMapRuleType type = CoverageMapRuleType.Include,
        CoverageMatchMode mode = CoverageMatchMode.Contains,
        CoverageAddressMatchComponent allowed = CoverageAddressMatchComponent.SafeAreaDefault,
        int priority = 100,
        string name = "test-rule")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            MatchText = matchText,
            RuleType = type,
            MatchMode = mode,
            AllowedComponents = allowed,
            IsActive = true,
            Priority = priority,
        };

    // The exact Google-parsed shape the SmartFuture website now sends
    // for "Giyani Rd, Giyani-E, Giyani, 0826, South Africa". Suburb =
    // Giyani-E (sublocality), City = Giyani (locality), Town = Giyani
    // (mirrors city when admin_area_2 = locality), Country = South
    // Africa, PostalCode = 0826.
    private static CoverageCheckRequestDto GiyaniStructured() => new()
    {
        AddressText      = "Giyani Rd, Giyani-E, Giyani, 0826, South Africa",
        AddressLine1     = "Giyani Rd",
        StreetName       = "Giyani Rd",
        Suburb           = "Giyani-E",
        City             = "Giyani",
        Town             = "Giyani",
        Province         = "Limpopo",
        PostalCode       = "0826",
        Country          = "South Africa",
        FormattedAddress = "Giyani Rd, Giyani-E, Giyani, 0826, South Africa",
    };

    // Adversarial: an address in Pretoria (Gauteng) whose STREET is
    // "Giyani Road". Structured city/province do NOT contain "Giyani".
    // Locks the false-positive prevention that the "Safe Area" bundle
    // exists to guarantee.
    private static CoverageCheckRequestDto CenturionOnGiyaniRoad() => new()
    {
        AddressText      = "12 Giyani Road, Pretoria, Gauteng, South Africa",
        AddressLine1     = "12 Giyani Road",
        StreetName       = "Giyani Road",
        Suburb           = "Waterkloof",
        City             = "Pretoria",
        Town             = "Pretoria",
        Province         = "Gauteng",
        Country          = "South Africa",
        FormattedAddress = "12 Giyani Road, Pretoria, Gauteng, South Africa",
    };

    // ─── Safe-area matches (the Giyani bug fix) ──────────────────────

    [Fact]
    public void CoverageRule_IncludeContains_MatchesCity_Giyani()
    {
        var rule = Rule("Giyani");
        var hit  = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, GiyaniStructured());

        hit.Should().NotBeNull();
        hit!.Matched.Should().BeTrue();
        hit.MatchedType.Should().Be(CoverageMapRuleType.Include);
        hit.MatchedRuleId.Should().Be(rule.Id);
        // Suburb comes BEFORE City in the candidate order — Giyani-E
        // contains "giyani" too, so the matcher legitimately trips on
        // Suburb first. Both would be correct outcomes; we just lock
        // that SOME structured field wins (not FullAddress / StreetName).
        hit.MatchedComponent.Should().BeOneOf(
            CoverageAddressMatchComponent.Suburb,
            CoverageAddressMatchComponent.City,
            CoverageAddressMatchComponent.Town);
    }

    [Fact]
    public void CoverageRule_IncludeContains_MatchesTown_Giyani()
    {
        // A request that fills Town but not City (rare Google shape)
        // — the "Match on Town" component must still trip.
        var req = GiyaniStructured();
        req.City = null;
        req.Suburb = null;
        var rule = Rule("Giyani");

        var hit = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);

        hit.Should().NotBeNull();
        hit!.MatchedComponent.Should().Be(CoverageAddressMatchComponent.Town);
    }

    [Fact]
    public void CoverageRule_IncludeContains_MatchesSuburb_GiyaniE()
    {
        // A request that ONLY has Suburb (e.g. address wizard step 1) —
        // the matcher must still catch it.
        var req = new CoverageCheckRequestDto { Suburb = "Giyani-E" };
        var rule = Rule("Giyani");

        var hit = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);

        hit.Should().NotBeNull();
        hit!.MatchedComponent.Should().Be(CoverageAddressMatchComponent.Suburb);
        hit.MatchedValue.Should().Be("Giyani-E");
    }

    // ─── Risky-component gating (Test 2 in the acceptance sheet) ─────

    [Fact]
    public void CoverageRule_DoesNotMatchFormattedAddress_WhenFormattedAddressNotAllowed()
    {
        // Safe-area rule only — FormattedAddress NOT ticked. The
        // Pretoria address contains "Giyani" only in the street /
        // formatted-address fields. Must NOT match.
        var rule = Rule("Giyani");
        var hit  = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, CenturionOnGiyaniRoad());
        hit.Should().BeNull();
    }

    [Fact]
    public void CoverageRule_MatchesFormattedAddress_OnlyWhenFormattedAddressAllowed()
    {
        // Admin intentionally opts into FormattedAddress → the same
        // Pretoria address WOULD match. This is the "you asked for it"
        // path.
        var rule = Rule("Giyani",
            allowed: CoverageAddressMatchComponent.SafeAreaDefault
                    | CoverageAddressMatchComponent.FormattedAddress);
        var hit  = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, CenturionOnGiyaniRoad());

        hit.Should().NotBeNull();
        hit!.MatchedComponent.Should().Be(CoverageAddressMatchComponent.FormattedAddress);
    }

    [Fact]
    public void CoverageRule_DoesNotMatchStreetName_WhenStreetNameNotAllowed()
    {
        // Only StreetName carries "Giyani" — must not trip a safe-area
        // rule.
        var req = new CoverageCheckRequestDto
        {
            StreetName = "Giyani Road",
            City       = "Pretoria",
        };
        var rule = Rule("Giyani");
        var hit  = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);
        hit.Should().BeNull();
    }

    [Fact]
    public void CoverageRule_MatchesStreetName_OnlyWhenStreetNameAllowed()
    {
        var req = new CoverageCheckRequestDto
        {
            StreetName = "Giyani Road",
            City       = "Pretoria",
        };
        var rule = Rule("Giyani",
            allowed: CoverageAddressMatchComponent.SafeAreaDefault
                    | CoverageAddressMatchComponent.StreetName);
        var hit = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);

        hit.Should().NotBeNull();
        hit!.MatchedComponent.Should().Be(CoverageAddressMatchComponent.StreetName);
    }

    // ─── Exclude wins over Include ───────────────────────────────────

    [Fact]
    public void CoverageRule_ExclusionWinsOverInclusion()
    {
        // Caller passes Excludes FIRST — the matcher trips on the
        // exclude and never even sees the include. This is the contract
        // the CoverageMapRuleService wrapper enforces, and it's what
        // guarantees a narrow no-service pocket inside a broadly
        // included region wins.
        var exclude = Rule("Giyani-E", type: CoverageMapRuleType.Exclude, name: "no-giyani-e");
        var include = Rule("Giyani",   type: CoverageMapRuleType.Include, name: "yes-giyani");

        var excludeHit = CoverageMapRuleMatcher.FindFirstMatch(new[] { exclude }, GiyaniStructured());
        excludeHit.Should().NotBeNull();
        excludeHit!.MatchedType.Should().Be(CoverageMapRuleType.Exclude);

        // If somehow the caller only passed the include after excludes
        // came up empty, it would still trip — proving the wrapper's
        // ORDER is the source of the "exclude wins" behaviour.
        var includeHit = CoverageMapRuleMatcher.FindFirstMatch(new[] { include }, GiyaniStructured());
        includeHit.Should().NotBeNull();
        includeHit!.MatchedType.Should().Be(CoverageMapRuleType.Include);
    }

    // ─── Match-mode semantics ────────────────────────────────────────

    [Fact]
    public void CoverageRule_Contains_IsCaseInsensitive()
    {
        var req = new CoverageCheckRequestDto { City = "GIYANI" };
        var rule = Rule("giyani");
        var hit  = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);
        hit.Should().NotBeNull();
    }

    [Fact]
    public void CoverageRule_Contains_TrimsAndCollapsesWhitespace()
    {
        // Locks NormalizeForMatch: trim + collapse whitespace + lower.
        var req = new CoverageCheckRequestDto { City = "  Giyani   " };
        var rule = Rule("  giyani  ");
        var hit  = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);
        hit.Should().NotBeNull();
    }

    [Fact]
    public void CoverageRule_Exact_RequiresExactNormalizedMatch()
    {
        // Exact mode against a Suburb of "Giyani-E" with rule text
        // "Giyani" must NOT match — the whole string has to line up
        // after normalisation.
        var req  = new CoverageCheckRequestDto { Suburb = "Giyani-E" };
        var rule = Rule("Giyani", mode: CoverageMatchMode.Exact);
        var hit  = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);
        hit.Should().BeNull();

        // But an exact match on City="Giyani" trips.
        var req2 = new CoverageCheckRequestDto { City = "Giyani" };
        var hit2 = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req2);
        hit2.Should().NotBeNull();
    }

    [Fact]
    public void CoverageRule_StartsWith_MatchesLeadingText()
    {
        var req  = new CoverageCheckRequestDto { City = "Giyani East" };
        var rule = Rule("Giyani", mode: CoverageMatchMode.StartsWith);
        var hit  = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);
        hit.Should().NotBeNull();
    }

    // ─── Postal code / country ───────────────────────────────────────

    [Fact]
    public void CoverageRule_PostalCode_MatchesWhenAllowed()
    {
        var req = new CoverageCheckRequestDto { PostalCode = "0826" };
        var rule = Rule("0826",
            allowed: CoverageAddressMatchComponent.PostalCode);
        var hit = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);
        hit.Should().NotBeNull();
        hit!.MatchedComponent.Should().Be(CoverageAddressMatchComponent.PostalCode);
    }

    [Fact]
    public void CoverageRule_Country_MatchesWhenAllowed()
    {
        var req = new CoverageCheckRequestDto { Country = "South Africa" };
        var rule = Rule("South Africa",
            allowed: CoverageAddressMatchComponent.Country);
        var hit = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, req);
        hit.Should().NotBeNull();
        hit!.MatchedComponent.Should().Be(CoverageAddressMatchComponent.Country);
    }

    // ─── Safety rails: empty inputs, empty match text ────────────────

    [Fact]
    public void CoverageRule_EmptyRules_ReturnsNull()
    {
        var hit = CoverageMapRuleMatcher.FindFirstMatch(
            Array.Empty<CoverageMapRule>(), GiyaniStructured());
        hit.Should().BeNull();
    }

    [Fact]
    public void CoverageRule_EmptyRequest_ReturnsNull()
    {
        var rule = Rule("Giyani");
        var hit = CoverageMapRuleMatcher.FindFirstMatch(
            new[] { rule }, new CoverageCheckRequestDto());
        hit.Should().BeNull();
    }

    [Fact]
    public void CoverageRule_WhitespaceMatchText_SkipsRule()
    {
        // A misconfigured rule with all-whitespace MatchText must NOT
        // become a wildcard-match — normalize returns empty, matcher
        // skips.
        var rule = Rule("   ");
        var hit  = CoverageMapRuleMatcher.FindFirstMatch(new[] { rule }, GiyaniStructured());
        hit.Should().BeNull();
    }

    // ─── Normalisation primitive locks ────────────────────────────────

    [Theory]
    [InlineData(null,           "")]
    [InlineData("",             "")]
    [InlineData("   ",          "")]
    [InlineData("Giyani",       "giyani")]
    [InlineData("  Giyani ",    "giyani")]
    [InlineData("GIYANI  east", "giyani east")]
    public void CoverageRule_NormalizeForMatch_ProducesStableTargets(string? input, string expected)
        => CoverageMapRuleMatcher.NormalizeForMatch(input).Should().Be(expected);

    [Theory]
    [InlineData("giyani",          "giyani", CoverageMatchMode.Exact,      true)]
    [InlineData("giyani-e",        "giyani", CoverageMatchMode.Exact,      false)]
    [InlineData("giyani-e",        "giyani", CoverageMatchMode.Contains,   true)]
    [InlineData("east giyani",     "giyani", CoverageMatchMode.StartsWith, false)]
    [InlineData("giyani east",     "giyani", CoverageMatchMode.StartsWith, true)]
    public void CoverageRule_IsMatch_LocksModeSemantics(
        string haystack, string needle, CoverageMatchMode mode, bool expected)
        => CoverageMapRuleMatcher.IsMatch(haystack, needle, mode).Should().Be(expected);
}

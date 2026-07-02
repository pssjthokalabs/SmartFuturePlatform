using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Coverage.Providers;
using SmartFuture.Application.ServicePackages;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Shared.Enums.Coverage;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;

namespace SmartFuture.Tests.Coverage;

// Phase-8 tests locking the Coverage-Map INCLUDE override path.
//
// Contract (see CoverageCheckService.CheckInternalAsync + the
// `if (mapHit.Matched)` short-circuit):
//   1. An Include rule bypasses Openserve entirely. Fibre provider must
//      NEVER be called.
//   2. The response MUST carry MatchSource=CoverageMapInclude so the
//      website can distinguish admin-authored coverage from Openserve.
//   3. AvailablePackages MUST be populated with every ACTIVE public
//      Fibre package. No line-speed gate — the admin promised the
//      location works, and there's no Openserve reading to filter by.
//   4. When there are no active Fibre packages configured at all, the
//      list is empty. The website's existing empty-state copy applies.
//   5. An Exclude rule also bypasses Openserve, but leaves the package
//      list empty — nothing to order.
public class CoverageCheckServiceMapOverrideTests
{
    private sealed record Harness(
        CoverageCheckService                 Service,
        Mock<IGeocodingService>              Geocoding,
        Mock<IFibreCoverageProvider>         FibreProvider,
        Mock<IServicePackageService>         Packages,
        Mock<ICoverageMapRuleService>        CoverageMap);

    private static Harness Build(
        CoverageMapEvaluationResult mapResult,
        IReadOnlyList<ServicePackageDto>? fibrePackages = null)
    {
        // FibreProvider is Strict — any call fails the test. This is the
        // primary contract of a map-override: Openserve is skipped.
        var fibre = new Mock<IFibreCoverageProvider>(MockBehavior.Strict);
        var geo   = new Mock<IGeocodingService>(MockBehavior.Strict);

        var packages = new Mock<IServicePackageService>(MockBehavior.Loose);
        packages
            .Setup(x => x.SearchCustomerAsync(It.IsAny<ServicePackageFilterRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ServicePackageFilterRequestDto f, CancellationToken _) =>
            {
                var items = (fibrePackages ?? Array.Empty<ServicePackageDto>())
                    .Where(p => f.Type == null || p.Type == f.Type)
                    .ToList();
                return Result<PagedResult<ServicePackageDto>>.Success(
                    new PagedResult<ServicePackageDto>(items, 1, 100, items.Count));
            });

        var coverageMap = new Mock<ICoverageMapRuleService>(MockBehavior.Loose);
        coverageMap
            .Setup(x => x.TryEvaluateAsync(It.IsAny<CoverageCheckRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mapResult);

        var env = new Mock<IHostEnvironment>(MockBehavior.Loose);
        env.SetupGet(x => x.EnvironmentName).Returns("Development");

        var svc = new CoverageCheckService(
            geo.Object, fibre.Object, packages.Object,
            coverageMap.Object, env.Object,
            NullLogger<CoverageCheckService>.Instance);

        return new Harness(svc, geo, fibre, packages, coverageMap);
    }

    private static ServicePackageDto ActiveFibre(
        string name, decimal price, int? downMbps = 100, int order = 1) =>
        new()
        {
            Id                  = Guid.NewGuid(),
            Type                = ServicePackageType.Fibre,
            Status              = ServicePackageStatus.Active,
            Name                = name,
            Price               = price,
            DownloadSpeedMbps   = downMbps,
            UploadSpeedMbps     = downMbps,
            DisplayOrder        = order,
            BillingCycle        = ServicePackageBillingCycle.Monthly,
            HasFreeInstallation = false,
            IncludesRouter      = true,
        };

    private static CoverageCheckRequestDto GiyaniStructured() => new()
    {
        City     = "Giyani",
        Suburb   = "Giyani-E",
        Country  = "South Africa",
        // Set enough for the "has address" guard, but no lat/lon —
        // we specifically want to prove the map override short-circuits
        // BEFORE any geocoding attempt.
    };

    // ─── Include rule ────────────────────────────────────────────────

    [Fact]
    public async Task CoverageCheck_ManualInclude_ReturnsCoverageSourceCoverageMapRule()
    {
        var ruleId = Guid.NewGuid();
        var h = Build(new CoverageMapEvaluationResult
            {
                Matched          = true,
                MatchedType      = CoverageMapRuleType.Include,
                MatchedRuleId    = ruleId,
                MatchedRuleName  = "Giyani Test",
                MatchedComponent = CoverageAddressMatchComponent.City,
                MatchedValue     = "Giyani",
            },
            new[] { ActiveFibre("Fibre 100", 375m) });

        var result = await h.Service.CheckAsync(GiyaniStructured());

        result.IsSuccess.Should().BeTrue();
        result.Data!.CoverageAvailable.Should().BeTrue();
        result.Data.MatchSource.Should().Be(CoverageMatchSource.CoverageMapInclude);
        result.Data.MatchedRuleId.Should().Be(ruleId);
        result.Data.MatchedRuleName.Should().Be("Giyani Test");
        result.Data.RawStatus.Should().Be("CoverageMapInclude");
    }

    [Fact]
    public async Task CoverageCheck_ManualInclude_SkipsOpenserve()
    {
        var h = Build(new CoverageMapEvaluationResult
        {
            Matched         = true,
            MatchedType     = CoverageMapRuleType.Include,
            MatchedRuleId   = Guid.NewGuid(),
            MatchedRuleName = "Giyani Test",
        });

        var result = await h.Service.CheckAsync(GiyaniStructured());

        result.IsSuccess.Should().BeTrue();
        // Both must be untouched — Strict mocks would throw on any call.
        h.FibreProvider.VerifyNoOtherCalls();
        h.Geocoding.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CoverageCheck_ManualInclude_ReturnsCoveredWithAllActiveFibrePackages()
    {
        // Three active Fibre packages at wildly different speeds. On the
        // Openserve path the 1 Gbps package would be filtered against a
        // line-speed — here it MUST come through untouched because the
        // admin promised the location works.
        var packages = new[]
        {
            ActiveFibre("Fibre 100",  375m, downMbps: 100,   order: 1),
            ActiveFibre("Fibre 500",  699m, downMbps: 500,   order: 2),
            ActiveFibre("Fibre 1Gig", 999m, downMbps: 1_000, order: 3),
        };
        var h = Build(new CoverageMapEvaluationResult
        {
            Matched         = true,
            MatchedType     = CoverageMapRuleType.Include,
            MatchedRuleId   = Guid.NewGuid(),
            MatchedRuleName = "Giyani Test",
        }, packages);

        var result = await h.Service.CheckAsync(GiyaniStructured());

        result.IsSuccess.Should().BeTrue();
        var dto = result.Data!;
        dto.AvailablePackages.Should().HaveCount(3);
        // Ordered by DisplayOrder, then Price — same rule as the
        // Openserve path (MatchPackagesAsync uses the same sort).
        dto.AvailablePackages.Select(p => p.Name).ToArray()
            .Should().Equal(new[] { "Fibre 100", "Fibre 500", "Fibre 1Gig" });
        dto.AvailablePackages.Should().OnlyContain(p =>
            p.MatchReason == "Available in this area");
        h.FibreProvider.VerifyNoOtherCalls();

        // The service package search must have asked for Fibre + Active
        // only — not "everything" — so admins with a Security-shape
        // Include rule wouldn't accidentally pull Security packages.
        h.Packages.Verify(x => x.SearchCustomerAsync(
            It.Is<ServicePackageFilterRequestDto>(f =>
                f.Type == ServicePackageType.Fibre
                && f.StatusFilter == ServicePackageStatus.Active),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CoverageCheck_ManualInclude_NoActivePackages_ReturnsEmptyList()
    {
        // Admin flipped every Fibre package inactive. Coverage stays
        // available (the include rule authored it) but the package list
        // is legitimately empty. Website falls through to the empty-state
        // "no packages configured yet" copy.
        var h = Build(new CoverageMapEvaluationResult
        {
            Matched         = true,
            MatchedType     = CoverageMapRuleType.Include,
            MatchedRuleId   = Guid.NewGuid(),
            MatchedRuleName = "Giyani Test",
        }, fibrePackages: Array.Empty<ServicePackageDto>());

        var result = await h.Service.CheckAsync(GiyaniStructured());

        result.IsSuccess.Should().BeTrue();
        result.Data!.CoverageAvailable.Should().BeTrue();
        result.Data.AvailablePackages.Should().BeEmpty();
        h.FibreProvider.VerifyNoOtherCalls();
    }

    // ─── Exclude rule ────────────────────────────────────────────────

    [Fact]
    public async Task CoverageCheck_ManualExclude_ReturnsUnavailable_WithNoPackages()
    {
        var h = Build(new CoverageMapEvaluationResult
            {
                Matched         = true,
                MatchedType     = CoverageMapRuleType.Exclude,
                MatchedRuleId   = Guid.NewGuid(),
                MatchedRuleName = "Giyani Blocklist",
            },
            new[] { ActiveFibre("Fibre 100", 375m) });

        var result = await h.Service.CheckAsync(GiyaniStructured());

        result.IsSuccess.Should().BeTrue();
        result.Data!.CoverageAvailable.Should().BeFalse();
        result.Data.MatchSource.Should().Be(CoverageMatchSource.CoverageMapExclude);
        result.Data.AvailablePackages.Should().BeEmpty(
            "an exclude rule blocks the address — no packages to offer");
        h.FibreProvider.VerifyNoOtherCalls();
        // Even the package lookup must be skipped on exclude — there's
        // nothing to display.
        h.Packages.Verify(x => x.SearchCustomerAsync(
            It.IsAny<ServicePackageFilterRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Rule-service failure isolation ──────────────────────────────

    [Fact]
    public async Task CoverageCheck_ManualInclude_PackageLookupThrows_StillReturnsCovered()
    {
        // If IServicePackageService.SearchCustomerAsync somehow throws
        // (DB down, DI wiring bug), the response must still declare the
        // location covered — the admin rule is the source of truth.
        // Packages just come through empty.
        var h = Build(new CoverageMapEvaluationResult
        {
            Matched         = true,
            MatchedType     = CoverageMapRuleType.Include,
            MatchedRuleId   = Guid.NewGuid(),
            MatchedRuleName = "Giyani Test",
        });
        h.Packages
            .Setup(x => x.SearchCustomerAsync(It.IsAny<ServicePackageFilterRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db-down"));

        var result = await h.Service.CheckAsync(GiyaniStructured());

        result.IsSuccess.Should().BeTrue();
        result.Data!.CoverageAvailable.Should().BeTrue();
        result.Data.MatchSource.Should().Be(CoverageMatchSource.CoverageMapInclude);
        result.Data.AvailablePackages.Should().BeEmpty();
        h.FibreProvider.VerifyNoOtherCalls();
    }
}

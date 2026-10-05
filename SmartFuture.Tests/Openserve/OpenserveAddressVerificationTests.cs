using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.API.Controllers;
using SmartFuture.API.Extensions;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Installations;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Tests.Infrastructure;
using Xunit;
using F = SmartFuture.Tests.Openserve.OpenserveEvidenceFixtures;
using H = SmartFuture.Tests.Openserve.OpenserveQualificationOrchestrationTests;

namespace SmartFuture.Tests.Openserve;

// Staging, LAT -25.866217 LON 28.106903 (the Google pin of "2 Palmas Street,
// Thorn Field Estate, Centurion") with FORCEVERIFY=Y returned AddressVerify[]:
//   52782141  8 PALMAS ST MONAVONI X 6 CENTURION     DIST 0
//   80573005  11A DE OVALLE BLV MONAVONI X 6 CENTURION  26.73 m
//   52782142  11 DE OVALLE BLV MONAVONI X 6 CENTURION   26.73 m
// Google/municipal and Openserve Address Master identities disagree even at
// the same coordinates. These pin the resolution rules: the premises is the
// one candidate that matches the customer's street number + street (distance
// never decides), or one an Admin explicitly chose; then that AMID — and only
// that AMID — is qualified. No match → premises unresolved → Fibre UNKNOWN
// (never "no Fibre"), no payment, no Product Order.
public class OpenserveAddressVerificationTests
{
    private const string Pin8 = "52782141";
    private const string Ovalle11A = "80573005";
    private const string Ovalle11 = "52782142";
    private const decimal PinLat = -25.866217m;
    private const decimal PinLon = 28.106903m;

    private static readonly OpenserveAddressMatcher.CustomerAddress TwoPalmas = new("2 Palmas Street", "Thorn Field Estate", "Centurion", "Gauteng");

    private static OpenserveAddressCandidate C(string amid, string address, decimal dist = 0m) => new(amid, dist, $"{dist:0.00} m", address, PinLat, PinLon);

    private static IReadOnlyList<OpenserveAddressCandidate> StagingCandidates() =>
        F.FromJson(F.UatAddressVerifyResponse).Outcome!.Facts!.AddressCandidates!;

    // ─── 1–6. Parsing and matching ──────────────────────────────────

    [Fact]
    public void RealStagingAddressVerify_TwoPalmas_NoCandidateMatches_AndEachVerdictIsExplained()
    {
        var resolution = OpenserveAddressCandidateMatcher.Resolve(TwoPalmas, StagingCandidates());

        Assert.Equal(OpenserveAddressResolution.Unresolved, resolution.Resolution);
        Assert.Null(resolution.Selected);
        Assert.Equal(new[] { Pin8, Ovalle11A, Ovalle11 }, resolution.Assessments.Select(a => a.Candidate.Amid));
        Assert.Equal(new[] { OpenserveCandidateMatch.StreetNumberMismatch, OpenserveCandidateMatch.StreetMismatch, OpenserveCandidateMatch.StreetMismatch },
            resolution.Assessments.Select(a => a.Match));
        Assert.Contains("(8 vs 2)", resolution.Assessments[0].Detail);
        Assert.Contains("None of the 3", resolution.Detail);
    }

    [Fact]
    public void DistanceZero_WithAnotherHouseNumber_IsNeverAMatch()
    {
        var resolution = OpenserveAddressCandidateMatcher.Resolve(TwoPalmas, new[] { C(Pin8, "8 PALMAS ST MONAVONI X 6 CENTURION", 0m) });

        Assert.Equal(OpenserveAddressResolution.Unresolved, resolution.Resolution);
        Assert.Equal(OpenserveCandidateMatch.StreetNumberMismatch, resolution.Assessments.Single().Match);
    }

    [Fact]
    public void ExactStreetNumberAndStreet_AutoResolves_EvenWhenItIsNotTheClosest()
    {
        var resolution = OpenserveAddressCandidateMatcher.Resolve(TwoPalmas, new[]
        {
            C(Pin8, "8 PALMAS ST MONAVONI X 6 CENTURION", 0m),
            C("52782199", "2 PALMAS ST MONAVONI X 6 CENTURION", 41.2m)
        });

        Assert.Equal(OpenserveAddressResolution.AutoMatched, resolution.Resolution);
        Assert.Equal("52782199", resolution.Selected!.Amid);
    }

    [Fact]
    public void SeveralCandidatesMatching_AreNeverGuessed()
    {
        var resolution = OpenserveAddressCandidateMatcher.Resolve(TwoPalmas, new[]
        {
            C("52782199", "2 PALMAS ST MONAVONI X 6 CENTURION", 5m),
            C("52782200", "2 PALMAS ST MONAVONI X 6 CENTURION", 6m)
        });

        Assert.Equal(OpenserveAddressResolution.Unresolved, resolution.Resolution);
        Assert.Null(resolution.Selected);
        Assert.Contains("not guessed", resolution.Detail);
    }

    [Fact]
    public void TheSameAmidListedTwice_IsStillOnePremises()
    {
        var resolution = OpenserveAddressCandidateMatcher.Resolve(TwoPalmas, new[]
        {
            C("52782199", "2 PALMAS ST MONAVONI X 6 CENTURION", 5m),
            C("52782199", "2 PALMAS ST MONAVONI X 6 CENTURION", 5m)
        });

        Assert.Equal(OpenserveAddressResolution.AutoMatched, resolution.Resolution);
    }

    [Fact]
    public void NoCandidates_IsNoCandidates()
    {
        var resolution = OpenserveAddressCandidateMatcher.Resolve(TwoPalmas, Array.Empty<OpenserveAddressCandidate>());

        Assert.Equal(OpenserveAddressResolution.NoCandidates, resolution.Resolution);
        Assert.Null(resolution.Selected);
    }

    [Theory]
    [InlineData("Palmas Street", "2 PALMAS ST MONAVONI X 6 CENTURION", OpenserveCandidateMatch.NotComparable)]  // no number to match on
    [InlineData("2 Palmas Street", "2A PALMAS ST MONAVONI X 6 CENTURION", OpenserveCandidateMatch.StreetNumberMismatch)] // a sub-number is another property
    [InlineData("2 Palmas Street", "2 A PALMAS ST MONAVONI X 6 CENTURION", OpenserveCandidateMatch.StreetNumberMismatch)]
    [InlineData("11a De Ovalle Boulevard", "11A DE OVALLE BLV MONAVONI X 6 CENTURION", OpenserveCandidateMatch.Matched)]
    [InlineData("2 Palmas Street", "2 PALMAS ST WATERKLOOF PRETORIA", OpenserveCandidateMatch.LocalityMismatch)]
    [InlineData("Thorn Field Estate, 2 Palmas St", "2 PALMAS ST MONAVONI X 6 CENTURION", OpenserveCandidateMatch.Matched)]
    public void CandidateVerdicts(string customerLine1, string candidateAddress, OpenserveCandidateMatch expected)
    {
        var assessment = OpenserveAddressCandidateMatcher.Assess(new OpenserveAddressMatcher.CustomerAddress(customerLine1, "Thorn Field Estate", "Centurion", null),
            C("1", candidateAddress));

        Assert.Equal(expected, assessment.Match);
    }

    // ─── Existing failed order SF-20261004-66E5A8CE ──────────────────

    [Fact]
    public async Task ExistingOrder_WithTheOldNearestAmid_ReVerified_IsAddressUnresolved_NotFibreUnavailable_AndNothingIsSent()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, amid: Pin8); // AMID 52782141 from the old nearest-address lookup, no evidence
        var order = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == seeded.Order.Id);
        order.AddressLine1 = "2 Palmas Street, Thorn Field Estate";
        order.Suburb = "Thorn Field Estate";
        order.City = "Centurion";
        order.Latitude = PinLat;
        order.Longitude = PinLon;
        await fixture.AppDbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = H.ClientWithVerify(sent, F.FromJson(F.UatAddressVerifyResponse), F.Call(OpenserveQualificationParser.Parse(F.UatNoFtthResponse)));
        var qualification = H.Qualification(fixture.AppDbContext, client);
        var fulfilment = H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, client, qualification), qualification);

        var before = (await fulfilment.GetAsync(order.Id)).Data!;
        Assert.Equal("EvidenceMissing", before.Qualification.Status); // readable as-is; nothing touched it automatically
        H.VerifyQualifyCalls(client, Times.Never());

        var run = await fulfilment.RunQualificationAsync(order.Id);

        Assert.True(run.IsSuccess, run.Message);
        var q = run.Data!.Qualification;
        Assert.Equal("AddressUnresolved", q.Status);
        Assert.NotEqual("FtthUnavailable", q.Status);
        Assert.Null(q.AmId); // the nearest-address AMID is no longer taken as the customer's premises
        Assert.Equal(new[] { Pin8, Ovalle11A, Ovalle11 }, q.AddressCandidates.Select(c => c.Amid));
        Assert.Equal(OpenserveFulfilmentState.BlockedAddressUnresolved, run.Data.State);
        Assert.Empty(sent);
        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        var audit = await fixture.DbContext.AuditLogs.AsNoTracking().Where(a => a.ActionType == AuditActionType.OpenserveOrderQualificationRun && a.EntityId == order.Id).SingleAsync();
        Assert.Contains($"\"previousAmid\":\"{Pin8}\"", audit.MetadataJson); // the replaced AMID stays on record
    }

    // ─── 10–13 / 16. Resolved premises → AMID qualification → order ──

    private static async Task<(Guid UserId, ServicePackage Package)> PackageAsync(SqliteTestDbFixture fixture, string sku = "OFC", string capacity = "50", int download = 50)
    {
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"buyer-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"SmartFuture Fibre {download}");
        package.DownloadSpeedMbps = download;
        db.PackageOpenserveMappings.Add(new PackageOpenserveMapping
        {
            ServicePackageId = package.Id, Sku = sku, Capacity = capacity, CapacityUom = "Mbps", OpenserveProductName = OpenserveProductCatalogue.ProductNameFor(sku)!, IsEnabled = true
        });
        await db.SaveChangesAsync();
        return (user.Id, package);
    }

    private static OrderService Orders(SqliteTestDbFixture fixture, Guid userId, IOpenserveQualificationService qualification) =>
        new(fixture.AppDbContext, Mock.Of<IAuditService>(), H.CurrentUser(userId), Mock.Of<INotificationService>(), Mock.Of<INetworkAccountService>(), Mock.Of<IInstallationService>(),
            Mock.Of<ICoverageCheckService>(), qualification, Options.Create(new PaymentSettings()), Options.Create(new BillingSettings()), NullLogger<OrderService>.Instance);

    private static CreateOrderRequestDto TwoPalmasOrder(Guid packageId, string line1 = "2 Palmas Street", string? unit = null) => new()
    {
        ServicePackageId = packageId, FullName = "Jane Doe", Email = "jane@example.com", PhoneNumber = "0821234567", AddressLine1 = line1, Suburb = "Thorn Field Estate",
        City = "Centurion", Province = "Gauteng", PostalCode = "0157", Latitude = PinLat, Longitude = PinLon, PropertyType = PropertyType.House, UnitNumber = unit
    };

    /// <summary>Staging candidates plus a real "2 PALMAS ST" record; AMID 52782199 qualifies with <paramref name="facts"/>.</summary>
    private static Mock<IOpenserveApiClient> TwoPalmasListed(List<OpenserveCreateOrderCommand> sent, OpenserveQualificationFacts facts) =>
        H.ClientWithVerify(sent, F.Verify(
            (Pin8, "8 PALMAS ST MONAVONI X 6 CENTURION", 0m, PinLat, PinLon),
            ("52782199", "2 PALMAS ST MONAVONI X 6 CENTURION", 38.4m, -25.866500m, 28.106700m)), F.Call(facts));

    private static OpenserveQualificationFacts TwoPalmasFacts(params OpenserveFtthInfrastructure[] ftth) =>
        F.Facts("52782199", "2", "PALMAS", "ST", "MONAVONI X 6", "CENTURION", ftth.Length == 0 ? new[] { F.OwnNetwork() } : ftth, latitude: -25.866500m, longitude: 28.106700m);

    [Fact]
    public async Task MatchedCandidate_IsQualifiedByAmid_AndTheOrderCarriesThatAmid_ThenProductOrderUsesIt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (userId, package) = await PackageAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = TwoPalmasListed(sent, TwoPalmasFacts());
        var qualification = H.Qualification(fixture.AppDbContext, client);

        var created = await Orders(fixture, userId, qualification).CreateMineAsync(TwoPalmasOrder(package.Id));

        Assert.True(created.IsSuccess, created.Message);
        client.Verify(c => c.QualifyAsync(It.Is<OpenserveQualificationQuery>(q => q.ForceVerify && q.Latitude == PinLat && q.Longitude == PinLon && q.BuildingInfo), It.IsAny<CancellationToken>()),
            Times.Once);
        client.Verify(c => c.QualifyAsync(It.Is<OpenserveQualificationQuery>(q => q.Amid == "52782199" && q.BuildingInfo && !q.ForceVerify), It.IsAny<CancellationToken>()), Times.Once);
        var order = await fixture.DbContext.Orders.AsNoTracking().SingleAsync();
        Assert.Equal("52782199", order.OpenserveAmId);
        var evidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync(r => r.Id == order.OpenserveQualificationResultId);
        Assert.Equal(OpenserveAddressResolution.AutoMatched, evidence.AddressResolution);
        Assert.Equal("52782199", evidence.QueryAmid);
        Assert.Equal(2, evidence.AddressCandidateCount);

        // Paid → submitted: the Product Order targets the resolved premises.
        var tracked = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == order.Id);
        tracked.Status = OrderStatus.PaymentReceived;
        var account = TestEntityFactory.CreateNetworkAccount(fixture.AppDbContext, tracked, status: Shared.Enums.NetworkAccounts.NetworkAccountStatus.Pending);
        await fixture.AppDbContext.SaveChangesAsync();
        await H.Submission(fixture.AppDbContext, client, qualification).TrySubmitForOrderAsync(order.Id, account.Id);

        var command = Assert.Single(sent);
        Assert.Equal("52782199", command.Amid);
        Assert.Equal("2 PALMAS ST", command.Street1);
        Assert.Equal("-25.8665", command.Latitude);
    }

    [Fact]
    public async Task MatchedCandidate_WithoutFtth_IsAGenuineFtthUnavailable()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (userId, package) = await PackageAsync(fixture);
        var client = TwoPalmasListed(new List<OpenserveCreateOrderCommand>(), F.Facts("52782199", "2", "PALMAS", "ST", "MONAVONI X 6", "CENTURION", Array.Empty<OpenserveFtthInfrastructure>()));

        var result = await Orders(fixture, userId, H.Qualification(fixture.AppDbContext, client)).CreateMineAsync(TwoPalmasOrder(package.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.OPENSERVE_FTTH_UNAVAILABLE, result.Code);
        Assert.StartsWith("Fibre isn't available at your address yet.", result.Message);
        Assert.Empty(await fixture.DbContext.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task MatchedCandidate_WithFtth_ButNotTheMappedProduct_IsProductUnavailable()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (userId, package) = await PackageAsync(fixture);
        var client = TwoPalmasListed(new List<OpenserveCreateOrderCommand>(), TwoPalmasFacts(F.OwnNetwork(skus: new[] { "OWS" })));

        var result = await Orders(fixture, userId, H.Qualification(fixture.AppDbContext, client)).CreateMineAsync(TwoPalmasOrder(package.Id));

        Assert.Equal(ErrorCodes.OPENSERVE_PRODUCT_UNAVAILABLE, result.Code);
    }

    // ─── 7. Unresolved address can't reach payment / order ──────────

    [Fact]
    public async Task UnresolvedAddress_CannotCreateAFibreOrder_AndTheReasonIsNotNoFibre()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (userId, package) = await PackageAsync(fixture);
        var client = H.ClientWithVerify(new List<OpenserveCreateOrderCommand>(), F.FromJson(F.UatAddressVerifyResponse), F.Call(TwoPalmasFacts()));

        var result = await Orders(fixture, userId, H.Qualification(fixture.AppDbContext, client)).CreateMineAsync(TwoPalmasOrder(package.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED, result.Code);
        Assert.Equal($"{OpenserveEligibilityAssessment.UnresolvedTitle} {OpenserveEligibilityAssessment.UnresolvedMessage}", result.Message);
        Assert.Empty(await fixture.DbContext.Orders.AsNoTracking().ToListAsync());
        H.VerifyAmidQualifications(client, Times.Never()); // the nearest record's coverage is never consulted
        var evidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync();
        Assert.Equal(OpenserveQualificationPurpose.CheckoutGate, evidence.Purpose);
        Assert.Equal(OpenserveAddressResolution.Unresolved, evidence.AddressResolution);
    }

    // ─── 17. MDU building/unit at checkout ───────────────────────────

    [Fact]
    public async Task Mdu_SeveralUnits_CheckoutNeedsTheCustomersUnit_ThenAllowsIt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (userId, package) = await PackageAsync(fixture);
        var rows = new List<OpenserveQualificationBuilding>
        {
            new("52782199", "395208", "617914", "290107", "1", "PALMAS COURT", "GROUND"),
            new("52782199", "786154", "617914", "290107", "12", "PALMAS COURT", "GROUND")
        };
        var facts = F.Facts("52782199", "2", "PALMAS", "ST", "MONAVONI X 6", "CENTURION", buildings: rows, latitude: -25.866500m, longitude: 28.106700m);
        var client = TwoPalmasListed(new List<OpenserveCreateOrderCommand>(), facts);
        var orders = Orders(fixture, userId, H.Qualification(fixture.AppDbContext, client));

        var noUnit = await orders.CreateMineAsync(TwoPalmasOrder(package.Id));
        Assert.Equal(ErrorCodes.OPENSERVE_BUILDING_UNIT_REQUIRED, noUnit.Code);
        Assert.Equal(OpenserveQualificationService.BuildingUnitRequiredMessage, noUnit.Message);

        var withUnit = await orders.CreateMineAsync(TwoPalmasOrder(package.Id, unit: "12"));
        Assert.True(withUnit.IsSuccess, withUnit.Message);
        var order = await fixture.DbContext.Orders.AsNoTracking().SingleAsync();
        Assert.Equal("786154", order.OpenserveBuildingNumId);
    }

    // ─── 14–16. Admin choice → qualified by AMID → Product Order uses it ─

    [Fact]
    public async Task AdminChoice_IsQualifiedByAmid_NeverSends_AndASubsequentSendUsesExactlyThatAmid()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture);
        var order = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == seeded.Order.Id);
        order.AddressLine1 = "2 Palmas Street";
        order.Suburb = "Thorn Field Estate";
        order.City = "Centurion";
        order.Latitude = PinLat;
        order.Longitude = PinLon;
        await fixture.AppDbContext.SaveChangesAsync();
        var adminId = TestEntityFactory.CreateUser(fixture.AppDbContext, $"admin-{Guid.NewGuid():N}@example.com", "Sipho", "Admin").Id;
        await fixture.AppDbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        // 11A DE OVALLE has Fibre with OWC (the seeded mapping) — what the Admin confirmed with the customer.
        var ovalle = F.Facts(Ovalle11A, "11A", "DE OVALLE", "BLV", "MONAVONI X 6", "CENTURION", latitude: -25.866414m, longitude: 28.107057m);
        var client = H.ClientWithVerify(sent, F.FromJson(F.UatAddressVerifyResponse), F.Call(ovalle));
        var qualification = H.Qualification(fixture.AppDbContext, client, userId: adminId);
        var submission = H.Submission(fixture.AppDbContext, client, qualification, userId: adminId);
        var fulfilment = H.Fulfilment(fixture.AppDbContext, submission, qualification, userId: adminId);

        await fulfilment.RunQualificationAsync(order.Id);
        var chosen = await fulfilment.SelectAddressCandidateAsync(order.Id, Ovalle11A, "Customer confirmed by phone the house is 11A De Ovalle Blv (estate number 2).");

        Assert.True(chosen.IsSuccess, chosen.Message);
        Assert.Contains("Nothing was sent", chosen.Message);
        Assert.Equal("Orderable", chosen.Data!.Qualification.Status);
        Assert.Equal(Ovalle11A, chosen.Data.AmId);
        Assert.Empty(sent); // choosing never sends
        client.Verify(c => c.QualifyAsync(It.Is<OpenserveQualificationQuery>(q => q.Amid == Ovalle11A), It.IsAny<CancellationToken>()), Times.Once);
        var evidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync(r => r.Id == chosen.Data.Qualification.EvidenceId);
        Assert.Equal(OpenserveAddressResolution.AdminSelected, evidence.AddressResolution);
        Assert.Equal(adminId, evidence.AddressResolvedByUserId);
        Assert.NotNull(evidence.AddressResolvedAtUtc);
        Assert.Equal(3, evidence.AddressCandidateCount); // the verification it was chosen from is kept
        var audit = await fixture.DbContext.AuditLogs.AsNoTracking().SingleAsync(a => a.ActionType == AuditActionType.OpenserveAddressCandidateSelected);
        Assert.Equal(adminId, audit.ActorUserId);
        Assert.Contains("\"selectedAmid\":\"80573005\"", audit.MetadataJson);

        // An automatic run afterwards does not undo the Admin's choice.
        var automatic = await qualification.QualifyAndPersistAsync(order.Id, OpenserveQualificationTrigger.SubmissionSelfHeal, ignoreCooldown: true);
        Assert.Equal(OpenserveQualificationRunStatus.Skipped, automatic.Status);

        // Admin then sends: the Product Order targets exactly the chosen premises.
        await fulfilment.SubmitAsync(order.Id, confirmOutcomeUnknown: false);
        var command = Assert.Single(sent);
        Assert.Equal(Ovalle11A, command.Amid);
        Assert.Equal("11A DE OVALLE BLV", command.Street1);
    }

    [Fact]
    public async Task AdminReRun_KeepsAnEarlierAdminChoice_WhileOpenserveStillListsIt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture);
        var order = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == seeded.Order.Id);
        order.AddressLine1 = "2 Palmas Street";
        order.Latitude = PinLat;
        order.Longitude = PinLon;
        order.City = "Centurion";
        await fixture.AppDbContext.SaveChangesAsync();
        var ovalle = F.Facts(Ovalle11A, "11A", "DE OVALLE", "BLV", "MONAVONI X 6", "CENTURION");
        var client = H.ClientWithVerify(new List<OpenserveCreateOrderCommand>(), F.FromJson(F.UatAddressVerifyResponse), F.Call(ovalle));
        var qualification = H.Qualification(fixture.AppDbContext, client);
        var fulfilment = H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, client, qualification), qualification);

        await fulfilment.RunQualificationAsync(order.Id);
        await fulfilment.SelectAddressCandidateAsync(order.Id, Ovalle11A, "Confirmed with the customer on site visit.");
        var rerun = await fulfilment.RunQualificationAsync(order.Id);

        Assert.Equal(Ovalle11A, rerun.Data!.AmId);
        Assert.Equal("AdminSelected", rerun.Data.Qualification.AddressResolution);
    }

    // ─── 22. Historical orders stay readable ─────────────────────────

    [Fact]
    public async Task LegacyEvidence_FromBeforeVerification_IsReadable_AndAsksForAReRun()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, amid: H.Amid);
        var order = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == seeded.Order.Id);
        var legacy = await F.SeedEligibleEvidenceAsync(fixture.AppDbContext, order, F.Facts(H.Amid, suburb: null, town: "RANDBURG"));
        Assert.Equal(OpenserveAddressResolution.NotEvaluated, legacy.AddressResolution);
        var client = H.Client(new List<OpenserveCreateOrderCommand>());
        var qualification = H.Qualification(fixture.AppDbContext, client);

        var view = await H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, client, qualification), qualification).GetAsync(order.Id);

        Assert.True(view.IsSuccess, view.Message);
        var q = view.Data!.Qualification;
        Assert.Equal("NotEvaluated", q.AddressResolution);
        Assert.Empty(q.AddressCandidates);
        Assert.False(q.CanSelectAddressCandidate);
        Assert.Contains("re-run address verification", q.CannotSelectAddressCandidateReason);
        Assert.Equal("Orderable", q.Status); // its own street number + street matched — legacy rule still applies
    }

    // ─── 23. No credentials anywhere the verification is persisted or shown ─

    [Fact]
    public async Task VerificationEvidence_AdminView_AndLogs_NeverContainTheApiKey()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture);
        var order = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == seeded.Order.Id);
        order.AddressLine1 = "2 Palmas Street";
        order.Latitude = PinLat;
        order.Longitude = PinLon;
        await fixture.AppDbContext.SaveChangesAsync();
        var client = H.ClientWithVerify(new List<OpenserveCreateOrderCommand>(), F.FromJson(F.UatAddressVerifyResponse), F.Call(TwoPalmasFacts()));
        var qualification = H.Qualification(fixture.AppDbContext, client);

        var run = await H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, client, qualification), qualification).RunQualificationAsync(order.Id);

        var evidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync();
        var logs = await fixture.DbContext.OpenserveIntegrationLogs.AsNoTracking().ToListAsync();
        var everything = string.Join("\n", new[] { evidence.AddressCandidatesJson, JsonSerializer.Serialize(run.Data) }.Concat(logs.Select(l => l.RequestHeadersJson + l.ResponseBodyJson)));
        Assert.DoesNotContain(H.FakeApiKey, everything);
        Assert.DoesNotContain("api_key", evidence.AddressCandidatesJson);
        Assert.Contains("REDACTED", logs.Single().RequestHeadersJson);
    }

    // ─── 24. Coverage rate limiting ──────────────────────────────────

    [Fact]
    public void CoverageCheck_IsRateLimitedPerIp_AndTheWindowRejectsBeyondTheLimit()
    {
        var attribute = typeof(CoverageController).GetMethod(nameof(CoverageController.Check))!.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(RateLimitingExtensions.CoveragePolicy, attribute!.PolicyName);

        var defaults = RateLimitingExtensions.CoverageLimiterOptions(new ConfigurationBuilder().Build());
        Assert.Equal(20, defaults.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.Window);
        Assert.Equal(0, defaults.QueueLimit);

        var configured = RateLimitingExtensions.CoverageLimiterOptions(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:Coverage:PermitLimit"] = "3", ["RateLimiting:Coverage:WindowSeconds"] = "60" }).Build());
        using var limiter = new FixedWindowRateLimiter(configured);
        var leases = Enumerable.Range(0, 4).Select(_ => limiter.AttemptAcquire()).ToList();
        Assert.Equal(new[] { true, true, true, false }, leases.Select(l => l.IsAcquired));
    }
}

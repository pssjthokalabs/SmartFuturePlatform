using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Coverage.Providers;
using SmartFuture.Application.Installations;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.ServicePackages;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Coverage;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;
using Xunit;
using F = SmartFuture.Tests.Openserve.OpenserveEvidenceFixtures;
using H = SmartFuture.Tests.Openserve.OpenserveQualificationOrchestrationTests;

namespace SmartFuture.Tests.Openserve;

// UAT SF-20261004-66E5A8CE: Product Qualification returned an AMID but NO
// ftthInfrastructure; SmartFuture treated the AMID as success, submitted OFC
// 50, and Openserve answered HTTP 400 "No Coverage". The customer also asked
// for 2 Palmas Street and Openserve resolved 8 PALMAS ST (32.77 m away).
// These pin the fix: an AMID identifies an address only; Fibre, the mapped
// product/speed and the address must all be confirmed by the qualification
// response — at the coverage check, at checkout and before Create Order.
public class OpenserveFibreEligibilityTests
{
    private const string UatAmid = "52782141";

    // ─── harness ────────────────────────────────────────────────────

    private static Mock<IOpenserveApiClient> Client(List<OpenserveCreateOrderCommand> sent, params OpenserveApiCallResult<OpenserveQualificationOutcome>[] answers) =>
        H.Client(sent, answers);

    private static OpenserveApiCallResult<OpenserveQualificationOutcome> Uat() => F.Call(OpenserveQualificationParser.Parse(F.UatNoFtthResponse));

    /// <summary>The real staging FORCEVERIFY answer for the 2 Palmas coordinates (8 PALMAS / 11A DE OVALLE / 11 DE OVALLE).</summary>
    private static OpenserveApiCallResult<OpenserveQualificationOutcome> UatVerify() => F.FromJson(F.UatAddressVerifyResponse);

    /// <summary>Address verification answers with the real staging AddressVerify[]; AMID 52782141 qualifies as the real UAT response (no FTTH).</summary>
    private static Mock<IOpenserveApiClient> UatClient(List<OpenserveCreateOrderCommand> sent) => H.ClientWithVerify(sent, UatVerify(), Uat());

    /// <summary>The UAT address (8 PALMAS ST MONAVONI X 6 CENTURION) — but WITH Fibre offering <paramref name="ftth"/>.</summary>
    private static OpenserveQualificationFacts Palmas(params OpenserveFtthInfrastructure[] ftth) =>
        F.Facts(UatAmid, "8", "PALMAS", "ST", "MONAVONI X 6", "CENTURION", ftth.Length == 0 ? new[] { F.OwnNetwork() } : ftth, distance: 32.77m, latitude: -25.866217m, longitude: 28.106903m);

    private static async Task<H.Seeded> SeedPalmasOrderAsync(SqliteTestDbFixture fixture, string addressLine1 = "2 Palmas Street, Thorn Field Estate", string sku = "OFC",
        string capacity = "50", string capacityUom = "Mbps", int? packageDownloadMbps = 50)
    {
        var seeded = await H.SeedAsync(fixture);
        var db = fixture.AppDbContext;
        var order = await db.Orders.Include(o => o.ServicePackage).SingleAsync(o => o.Id == seeded.Order.Id);
        order.AddressLine1 = addressLine1;
        order.Suburb = "Monavoni";
        order.City = "Centurion";
        order.Province = "Gauteng";
        order.Latitude = -25.866300m;
        order.Longitude = 28.106700m;
        order.ServicePackage!.DownloadSpeedMbps = packageDownloadMbps;
        var mapping = await db.PackageOpenserveMappings.SingleAsync(m => m.ServicePackageId == order.ServicePackageId);
        mapping.Sku = sku;
        mapping.OpenserveProductName = OpenserveProductCatalogue.ProductNameFor(sku) ?? sku;
        mapping.Capacity = capacity;
        mapping.CapacityUom = capacityUom;
        await db.SaveChangesAsync();
        return seeded;
    }

    private static Task<OpenserveOrder> RecordAsync(SqliteTestDbFixture fixture, Guid orderId) => fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);

    private static PackageOpenserveMapping Mapping(string sku, string capacity, string uom = "Mbps") =>
        new() { Sku = sku, Capacity = capacity, CapacityUom = uom, OpenserveProductName = OpenserveProductCatalogue.ProductNameFor(sku) ?? sku, IsEnabled = true };

    private static OpenserveQualificationResult Evidence(OpenserveQualificationFacts facts, string addressLine1 = "8 Palmas Street", string? suburb = "Monavoni", string? city = "Centurion")
    {
        var evidence = OpenserveQualificationEvidence.Build(F.Call(facts), OpenserveQualificationPurpose.CheckoutGate, -25.8663m, 28.1067m, null, null, DateTime.UtcNow);
        OpenserveQualificationEvidence.Evaluate(evidence, new OpenserveAddressMatcher.CustomerAddress(addressLine1, suburb, city, "Gauteng"), null, null, null);
        return evidence;
    }

    // ─── 1. AMID returned, no ftthInfrastructure → Fibre unavailable ─

    [Fact]
    public void UatResponse_AmidWithoutFtthInfrastructure_IdentifiesTheAddress_ButFibreIsNotAvailable()
    {
        var facts = OpenserveQualificationParser.Parse(F.UatNoFtthResponse);

        Assert.True(facts.IsOk);
        Assert.Equal(UatAmid, facts.Address!.Amid);
        Assert.Equal("8 PALMAS ST MONAVONI X 6 CENTURION", facts.Address.FullAddress);
        Assert.Equal("8", facts.Address.StreetNumber);
        Assert.Equal("NORTH EASTERN", facts.Address.Region);
        Assert.Equal(32.77m, facts.Address.DistanceMeters);
        Assert.Empty(facts.Ftth);
        Assert.Equal(new[] { "OUC", "EOC" }, facts.EthernetProductCodes);

        var evidence = Evidence(facts);
        Assert.True(evidence.AddressIdentified);
        Assert.Equal(OpenserveFibreAvailability.NotReturned, evidence.FibreAvailability);
        Assert.Equal(-25.866217m, evidence.Latitude);

        var assessment = OpenserveFibreEligibility.Assess(evidence, Mapping("OFC", "50"), 50);
        Assert.False(assessment.IsEligible);
        Assert.Equal(OpenserveProductEligibility.FibreUnavailable, assessment.Product);
        Assert.Equal(OpenserveBlockedCodes.FibreUnavailable, assessment.Blocker!.Value.Code);
        Assert.Contains("no Fibre (FTTH)", assessment.Blocker.Value.Reason);
        Assert.Contains("An AMID identifies the address only", assessment.Blocker.Value.Reason);
    }

    [Fact]
    public void SpecShapedResponse_EveryFtthEntryIsRead_AndThirdPartyProductsAreNeverSubstituted()
    {
        const string json = """
            {"errorCode":0,"errorString":"OK","Results":{"payload":{
              "AddressInfo":{"AMID":"50782408","LR_STREET_NO":"4682","LR_STREET":"SYSIE","LR_STREET_TYPE":"ST","LR_SUBURB":"RAND PARK RIDGE X 88","LR_TOWN":"RANDBURG"},
              "ftthInfrastructure":{"ftthInfo":[
                {"FTTH_Status":"Working","FTTHServiceProviderID":"0","fibreMaxSpeed":500,"fibreMaxSpeedUnit":"Mbps",
                 "ftthProductInfo":[{"ProductName":"Openserve Webstream","ProductCode":"OWS","upstreamSpeed":"250 Mbps","downstreamSpeed":"500 Mbps"}]},
                {"FTTH_Status":"pending","FTTH_Type":"3rd_Party","FTTHServiceProviderID":"M_21","fibreMaxSpeed":500,"fibreMaxSpeedUnit":"Mbps",
                 "ftthProductInfo":[{"ProductName":"Openserve Fibre Connect Third Party","ProductCode":"OFCTP","upstreamSpeed":"250 Mbps","downstreamSpeed":"500 Mbps"}]}
              ]}}}}
            """;
        var facts = OpenserveQualificationParser.Parse(json);
        Assert.Equal(2, facts.Ftth.Count);
        Assert.Equal("3rd_Party", facts.Ftth[1].Type);
        Assert.False(facts.Ftth[1].IsImmediatelyAvailable);

        var evidence = Evidence(facts, "4682 Sysie Street", "Rand Park Ridge", "Randburg");
        Assert.Equal(OpenserveFibreAvailability.Available, evidence.FibreAvailability);
        Assert.Equal("OWS", evidence.AvailableProductCodes);

        var ofc = OpenserveFibreEligibility.Assess(evidence, Mapping("OFC", "50"), 50);
        Assert.Equal(OpenserveProductEligibility.ProductUnavailable, ofc.Product);
        Assert.Contains("does not offer OFC", ofc.ProductReason);
        Assert.Equal(OpenserveBlockedCodes.ProductUnavailable, ofc.Blocker!.Value.Code);

        Assert.True(OpenserveFibreEligibility.Assess(evidence, Mapping("OWS", "50"), 50).IsEligible);
    }

    [Fact]
    public void FtthOsInfoAlias_SingleObject_AndNumbersAsStrings_AreRead()
    {
        const string json = """
            {"errorCode":"0","Results":{"payload":{
              "AddressInfo":{"AMID":"1000497","LR_STREET_NO":"61","LR_STREET":"OAK","LR_STREET_TYPE":"AVE","LR_TOWN":"CENTURION","DIST_M":".00 m"},
              "ftthOSInfo":{"FTTH_Status":"Available","fibreMaxSpeed":"200","fibreMaxSpeedUnit":"Mbps",
                "ftthProductInfo":{"ProductName":"Openserve Fibre Connect","ProductCode":"OFC","upstreamSpeed":"100 Mbps","downstreamSpeed":"200 Mbps"}}}}}
            """;
        var facts = OpenserveQualificationParser.Parse(json);
        var ftth = Assert.Single(facts.Ftth);
        Assert.True(ftth.IsImmediatelyAvailable);
        Assert.Equal(200m, ftth.MaxSpeed);
        Assert.Equal(0m, facts.Address!.DistanceMeters);
        Assert.True(OpenserveFibreEligibility.Assess(Evidence(facts, "61 Oak Avenue", null, "Centurion"), Mapping("OFC", "200"), 200).IsEligible);
    }

    // ─── 2 / 3 / 4. Product-specific eligibility ────────────────────

    [Fact]
    public void FibreAvailable_ButMappedProductCodeAbsent_PackageUnavailable()
    {
        var evidence = Evidence(Palmas(F.OwnNetwork(skus: new[] { "OWS", "OFCP" })));
        var assessment = OpenserveFibreEligibility.Assess(evidence, Mapping("OFC", "50"), 50);

        Assert.Equal(OpenserveFibreAvailability.Available, assessment.Fibre);
        Assert.Equal(OpenserveProductEligibility.ProductUnavailable, assessment.Product);
        Assert.Contains("available: OWS, OFCP", assessment.ProductReason);
    }

    [Fact]
    public void ProductPresent_ButRequiredCapacityNotOffered_PackageUnavailable()
    {
        var evidence = Evidence(Palmas(F.OwnNetwork(down: "20 Mbps", up: "10 Mbps", maxSpeed: 20m, skus: new[] { "OFC" })));
        var assessment = OpenserveFibreEligibility.Assess(evidence, Mapping("OFC", "50"), 50);

        Assert.Equal(OpenserveProductEligibility.CapabilityUnavailable, assessment.Product);
        Assert.Contains("only up to 20 Mbps", assessment.ProductReason);
        Assert.False(assessment.IsEligible);
    }

    [Theory]
    [InlineData("OFC", "50", "Mbps", 50)]
    [InlineData("OFC", "300", "Mbps Lite", 300)]
    [InlineData("OWC", "20", "Mbps", 20)]
    public void MatchingProductAndCapability_Eligible(string sku, string capacity, string uom, int packageMbps)
    {
        var evidence = Evidence(Palmas(F.OwnNetwork(down: "500 Mbps", up: "250 Mbps", maxSpeed: 500m)));
        var assessment = OpenserveFibreEligibility.Assess(evidence, Mapping(sku, capacity, uom), packageMbps);

        Assert.True(assessment.IsEligible, assessment.ProductReason);
        Assert.Null(assessment.Blocker);
        Assert.Contains("covers the mapped", assessment.ProductReason);
    }

    [Theory]
    [InlineData("Future (Planned)")]
    [InlineData("Pre Order")]
    [InlineData("pending")]
    public void FtthReturnedButNotImmediatelyAvailable_IsNotOrderable(string status)
    {
        var evidence = Evidence(Palmas(F.OwnNetwork(status: status)));
        Assert.Equal(OpenserveFibreAvailability.NotYetAvailable, evidence.FibreAvailability);
        Assert.Equal(OpenserveProductEligibility.FibreUnavailable, OpenserveFibreEligibility.Assess(evidence, Mapping("OFC", "50"), 50).Product);
    }

    // ─── 5 / 6. Address matching ─────────────────────────────────────

    [Theory]
    [InlineData("2 Palmas Street, Thorn Field Estate", "Thorn Field Estate", "Centurion", OpenserveAddressMatch.Mismatch)]
    [InlineData("8 Palmas Street", "Monavoni", "Centurion", OpenserveAddressMatch.Matched)]
    [InlineData("8 Palmas St", "Monavoni Ext 6", null, OpenserveAddressMatch.Matched)]        // "X 6" / "Ext 6" extensions compare equal
    [InlineData("Thorn Field Estate, 8 Palmas Street", "Monavoni", "Centurion", OpenserveAddressMatch.Matched)]
    [InlineData("Palmas St", "Monavoni", "Centurion", OpenserveAddressMatch.ReviewRequired)]   // Google route-only pick: no number to confirm
    [InlineData("8 Kerk Street", "Monavoni", "Centurion", OpenserveAddressMatch.ReviewRequired)] // same number, different street
    [InlineData("8 Palmas Street", "Sandton", "Johannesburg", OpenserveAddressMatch.ReviewRequired)] // same street name, another area
    public void AddressMatch_StreetNumberAndStreetDecide_DistanceNeverDoes(string line1, string? suburb, string? city, OpenserveAddressMatch expected)
    {
        var evidence = OpenserveQualificationEvidence.Build(F.Call(Palmas()), OpenserveQualificationPurpose.CheckoutGate, null, null, null, null, DateTime.UtcNow);
        var (match, detail) = OpenserveAddressMatcher.Compare(new OpenserveAddressMatcher.CustomerAddress(line1, suburb, city, "Gauteng"), evidence);

        Assert.Equal(expected, match);
        Assert.Contains("32.77 m", detail);
    }

    [Fact]
    public void AddressMismatch_BlocksEvenWhenFibreAndProductAreAvailable_UntilAdminAccepts()
    {
        var evidence = Evidence(Palmas(), "2 Palmas Street, Thorn Field Estate", "Thorn Field Estate", "Centurion");
        Assert.Equal(OpenserveAddressMatch.Mismatch, evidence.AddressMatch);

        var before = OpenserveFibreEligibility.Assess(evidence, Mapping("OFC", "50"), 50);
        Assert.Equal(OpenserveProductEligibility.Eligible, before.Product);
        Assert.False(before.IsEligible);
        Assert.Equal(OpenserveFibreQualificationStatus.AddressReviewRequired, before.Status());
        Assert.Equal(OpenserveBlockedCodes.AddressReview, before.Blocker!.Value.Code);
        Assert.Contains("8 PALMAS ST", before.Blocker.Value.Reason);
        Assert.Contains("Fibre availability at the customer's address is therefore unknown", before.Blocker.Value.Reason);
        Assert.Equal(OpenserveEligibilityAssessment.UnresolvedTitle, before.CustomerText.Title);
        Assert.DoesNotContain(UatAmid, before.CustomerText.Message);

        evidence.AddressAcceptedAtUtc = DateTime.UtcNow;
        Assert.True(OpenserveFibreEligibility.Assess(evidence, Mapping("OFC", "50"), 50).IsEligible);
    }

    // ─── 9. Backend submission gate (frontend bypassed) ─────────────

    [Fact]
    public async Task Submission_UatCase_NoCandidateMatches_IsAddressUnresolved_NotFibreUnavailable_AndNeverPosts()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedPalmasOrderAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = UatClient(sent);

        var attempt = await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client))
            .SubmitAsync(new OpenserveSubmissionRequest(seeded.Order.Id, OpenserveSubmissionTrigger.AutomaticInitial) { NetworkAccountId = seeded.Account.Id });

        Assert.Equal(OpenserveSubmissionOutcome.Blocked, attempt.Data!.Outcome);
        Assert.False(attempt.Data.RequestSent);
        Assert.Empty(sent);
        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);

        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveBlockedCodes.AddressUnresolved, record.LastFailureCode);
        Assert.Contains("UNKNOWN", record.LastFailureMessage);
        Assert.DoesNotContain("Fibre is not available", record.LastFailureMessage);
        var order = await H.OrderAsync(fixture, seeded.Order.Id);
        Assert.Null(order.OpenserveAmId); // 8 PALMAS (DIST 0) is NOT taken as the customer's premises
        var evidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync(r => r.Id == order.OpenserveQualificationResultId);
        Assert.Equal(OpenserveAddressResolution.Unresolved, evidence.AddressResolution);
        Assert.Equal(3, evidence.AddressCandidateCount);
        Assert.Equal(OpenserveFibreAvailability.NotEvaluated, evidence.FibreAvailability); // unknown, never "not returned"
        Assert.Equal(OpenserveProductEligibility.NotEvaluated, evidence.ProductEligibility);
        Assert.Equal(seeded.Order.Id, evidence.OrderId);
        H.VerifyAmidQualifications(client, Times.Never()); // no candidate's coverage is even asked for
    }

    [Fact]
    public async Task Submission_AddressMatchesButNoFtth_IsBlockedAsFibreUnavailable()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedPalmasOrderAsync(fixture, addressLine1: "8 Palmas Street");
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = UatClient(sent);

        await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        Assert.Empty(sent);
        H.VerifyAmidQualifications(client, Times.Once()); // the matched AMID is qualified by AMID
        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveBlockedCodes.FibreUnavailable, record.LastFailureCode);
        Assert.Equal(OpenserveSubmissionFailureClass.Blocked, record.LastFailureClass);
    }

    [Fact]
    public async Task Submission_ProductNotOffered_IsBlocked_ForAutomaticAndAdminPaths()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedPalmasOrderAsync(fixture, addressLine1: "8 Palmas Street");
        var order = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == seeded.Order.Id);
        order.OpenserveAmId = UatAmid;
        await F.SeedEligibleEvidenceAsync(fixture.AppDbContext, order, Palmas(F.OwnNetwork(skus: new[] { "OWS" })));
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent);
        var qualification = H.Qualification(fixture.AppDbContext, client);
        var submission = H.Submission(fixture.AppDbContext, client, qualification);

        await submission.TrySubmitForOrderAsync(order.Id, seeded.Account.Id);
        var admin = await H.Fulfilment(fixture.AppDbContext, submission, qualification).SubmitAsync(order.Id, confirmOutcomeUnknown: false);

        Assert.Empty(sent);
        H.VerifyQualifyCalls(client, Times.Never()); // evidence is current — no re-qualification
        Assert.Equal(OpenserveBlockedCodes.ProductUnavailable, (await RecordAsync(fixture, order.Id)).LastFailureCode);
        // Admin Send runs the same gate: the attempt is recorded as blocked and nothing is sent.
        Assert.Contains("does not offer OFC", admin.Message);
        Assert.Equal(OpenserveFulfilmentState.BlockedProductUnavailable, admin.Data!.State);
        Assert.False(admin.Data.ManualSubmission.Allowed);
    }

    [Fact]
    public async Task Submission_Eligible_SendsOpenservesCanonicalAddress_NotGoogleData()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedPalmasOrderAsync(fixture, addressLine1: "8 Palmas Street, Thorn Field Estate");
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, F.Call(Palmas()));

        await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        var command = Assert.Single(sent);
        Assert.Equal(UatAmid, command.Amid);
        Assert.Equal("OFC", command.Sku);
        Assert.Equal("8 PALMAS ST", command.Street1);
        Assert.Equal("MONAVONI X 6", command.Suburb);
        Assert.Equal("CENTURION", command.City);
        Assert.Equal("GAUTENG", command.Region);
        Assert.Equal("-25.866217", command.Latitude); // Openserve's LR_LAT for the AMID, not the Google pin (-25.8663)
        Assert.Equal("28.106903", command.Longitude);
    }

    // ─── 7. MDU still needs the building/unit ────────────────────────

    [Fact]
    public async Task Mdu_SeveralUnits_FibreAvailable_StillBlockedUntilBuildingUnitResolved()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedPalmasOrderAsync(fixture, addressLine1: "8 Palmas Street");
        var rows = new List<OpenserveQualificationBuilding>
        {
            new(UatAmid, "395208", "617914", "290107", "1", "PALMAS COURT", "GROUND"),
            new(UatAmid, "786154", "617914", "290107", "12", "PALMAS COURT", "GROUND")
        };
        var facts = F.Facts(UatAmid, "8", "PALMAS", "ST", "MONAVONI X 6", "CENTURION", buildings: rows);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, F.Call(facts));

        await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        Assert.Empty(sent);
        Assert.Equal(OpenserveBlockedCodes.BuildingUnit, (await RecordAsync(fixture, seeded.Order.Id)).LastFailureCode);
    }

    // ─── 11. Historical orders ───────────────────────────────────────

    [Fact]
    public async Task LegacyOrder_AmidWithoutEvidence_IsReadable_ThenSelfHealsBeforeSubmission()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, amid: H.Amid); // AMID from the old code, no evidence row
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, H.Qualified());
        var qualification = H.Qualification(fixture.AppDbContext, client);
        var submission = H.Submission(fixture.AppDbContext, client, qualification);

        var view = await H.Fulfilment(fixture.AppDbContext, submission, qualification).GetAsync(seeded.Order.Id);
        Assert.True(view.IsSuccess, view.Message);
        Assert.Equal("EvidenceMissing", view.Data!.Qualification.Status);
        Assert.Equal(OpenserveFulfilmentState.BlockedQualification, view.Data.State);
        Assert.Equal("Re-run address verification & qualification", view.Data.Qualification.RunLabel);
        Assert.True(view.Data.Qualification.CanRun);

        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        H.VerifyQualifyCalls(client, Times.Once());
        Assert.Single(sent);
        Assert.NotNull((await H.OrderAsync(fixture, seeded.Order.Id)).OpenserveQualificationResultId);
    }

    // ─── Admin: the UAT order explained without raw logs; explicit premises choice ─

    [Fact]
    public async Task AdminView_UatCase_ShowsCandidates_AndAnExplicitAuditedChoiceQualifiesThatAmid_WithoutSending()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedPalmasOrderAsync(fixture);
        var adminId = TestEntityFactory.CreateUser(fixture.AppDbContext, $"admin-{Guid.NewGuid():N}@example.com", "Thandi", "Admin").Id;
        await fixture.AppDbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = UatClient(sent);
        var qualification = H.Qualification(fixture.AppDbContext, client, userId: adminId);
        var fulfilment = H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, client, qualification, userId: adminId), qualification, userId: adminId);

        var run = await fulfilment.RunQualificationAsync(seeded.Order.Id);

        Assert.True(run.IsSuccess, run.Message);
        Assert.Contains("not yet known", run.Message);
        var q = run.Data!.Qualification;
        Assert.Equal("AddressUnresolved", q.Status);
        Assert.Equal("Unresolved", q.AddressResolution);
        Assert.Null(q.AmId);
        Assert.StartsWith("2 Palmas Street", q.CustomerAddress);
        Assert.Equal(-25.8663m, q.CustomerLatitude);
        Assert.Equal(new[] { "52782141", "80573005", "52782142" }, q.AddressCandidates.Select(c => c.Amid));
        Assert.Equal(new[] { "StreetNumberMismatch", "StreetMismatch", "StreetMismatch" }, q.AddressCandidates.Select(c => c.Match));
        Assert.Equal(new decimal?[] { 0m, 26.73m, 26.73m }, q.AddressCandidates.Select(c => c.DistanceMeters));
        Assert.Equal("NotEvaluated", q.FibreAvailability);
        Assert.Equal("NotEvaluated", q.ProductEligibility);
        Assert.Equal(OpenserveFulfilmentState.BlockedAddressUnresolved, run.Data.State);
        Assert.False(run.Data.ManualSubmission.Allowed);
        Assert.True(q.CanSelectAddressCandidate, q.CannotSelectAddressCandidateReason);
        H.VerifyAmidQualifications(client, Times.Never());

        var noNote = await fulfilment.SelectAddressCandidateAsync(seeded.Order.Id, UatAmid, "ok");
        Assert.False(noNote.IsSuccess);
        var notACandidate = await fulfilment.SelectAddressCandidateAsync(seeded.Order.Id, "99999999", "Customer confirmed by phone.");
        Assert.False(notACandidate.IsSuccess);

        var chosen = await fulfilment.SelectAddressCandidateAsync(seeded.Order.Id, UatAmid, "Customer confirmed by phone they live at 8 Palmas St.");

        Assert.True(chosen.IsSuccess, chosen.Message);
        var after = chosen.Data!.Qualification;
        Assert.Equal(UatAmid, after.AmId);
        Assert.Equal("AdminSelected", after.AddressResolution);
        Assert.Equal("Thandi Admin", after.AddressResolvedBy);
        Assert.Contains("8 Palmas", after.AddressResolutionNote);
        Assert.True(after.AddressAccepted); // the explicit choice is the acceptance of the street-number difference
        // That premises IS now the customer's: its missing FTTH is a genuine "Fibre unavailable".
        Assert.Equal("FtthUnavailable", after.Status);
        Assert.Equal("NotReturned", after.FibreAvailability);
        Assert.Equal(OpenserveFulfilmentState.BlockedFibreUnavailable, chosen.Data.State);
        H.VerifyAmidQualifications(client, Times.Once()); // the chosen AMID was qualified by AMID
        Assert.Empty(sent);
        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        var audit = await fixture.DbContext.AuditLogs.AsNoTracking().SingleAsync(a => a.ActionType == AuditActionType.OpenserveAddressCandidateSelected && a.EntityId == seeded.Order.Id);
        Assert.Equal(adminId, audit.ActorUserId);
        Assert.Contains("8 PALMAS ST MONAVONI X 6 CENTURION", audit.MetadataJson);
        Assert.Contains("2 Palmas Street", audit.MetadataJson);
        Assert.Contains("StreetNumberMismatch", audit.MetadataJson);
        Assert.Contains(chosen.Data.Activity, a => a.Title.StartsWith("Openserve premises chosen by Thandi Admin", StringComparison.Ordinal));
    }

    // ─── Coverage check: authenticated qualification decides ─────────

    private sealed record CoverageHarness(CoverageCheckService Service, Mock<IFibreCoverageProvider> Gis, Mock<IOpenserveApiClient> Client, Guid OfcPackageId, Guid OwcPackageId);

    private static async Task<CoverageHarness> CoverageAsync(SqliteTestDbFixture fixture, OpenserveApiCallResult<OpenserveQualificationOutcome> answer,
        OpenserveApiCallResult<OpenserveQualificationOutcome>? verify = null)
    {
        var db = fixture.AppDbContext;
        var ofc = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: "SmartFuture Fibre 50/25");
        ofc.DownloadSpeedMbps = 50;
        var owc = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: "SmartFuture Fibre 20/10");
        owc.DownloadSpeedMbps = 20;
        db.PackageOpenserveMappings.Add(new PackageOpenserveMapping { ServicePackageId = ofc.Id, Sku = "OFC", Capacity = "50", CapacityUom = "Mbps", OpenserveProductName = "Openserve Fibre Connect", IsEnabled = true });
        db.PackageOpenserveMappings.Add(new PackageOpenserveMapping { ServicePackageId = owc.Id, Sku = "OWC", Capacity = "20", CapacityUom = "Mbps", OpenserveProductName = "Openserve Web Connect", IsEnabled = true });
        await db.SaveChangesAsync();

        var packages = new Mock<IServicePackageService>();
        packages.Setup(x => x.SearchCustomerAsync(It.IsAny<ServicePackageFilterRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PagedResult<ServicePackageDto>>.Success(new PagedResult<ServicePackageDto>(new List<ServicePackageDto>
            {
                new() { Id = ofc.Id, Type = ServicePackageType.Fibre, Status = ServicePackageStatus.Active, Name = ofc.Name, Price = 499m, DownloadSpeedMbps = 50, DisplayOrder = 1 },
                new() { Id = owc.Id, Type = ServicePackageType.Fibre, Status = ServicePackageStatus.Active, Name = owc.Name, Price = 299m, DownloadSpeedMbps = 20, DisplayOrder = 2 }
            }, 1, 100, 2)));
        var map = new Mock<ICoverageMapRuleService>();
        map.Setup(x => x.TryEvaluateAsync(It.IsAny<CoverageCheckRequestDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CoverageMapEvaluationResult());
        var gis = new Mock<IFibreCoverageProvider>(MockBehavior.Strict); // the public GIS lookup must not decide anymore
        var client = H.ClientWithVerify(new List<OpenserveCreateOrderCommand>(), verify, answer);
        var service = new CoverageCheckService(Mock.Of<IGeocodingService>(), gis.Object, packages.Object, map.Object, Mock.Of<IHostEnvironment>(), NullLogger<CoverageCheckService>.Instance,
            H.Qualification(db, client));
        return new CoverageHarness(service, gis, client, ofc.Id, owc.Id);
    }

    private static CoverageCheckRequestDto PalmasRequest(string line1) => new()
    {
        Latitude = -25.866300m, Longitude = 28.106700m, AddressLine1 = line1, Suburb = "Monavoni", City = "Centurion", Province = "Gauteng",
        FormattedAddress = $"{line1}, Monavoni, Centurion, 0157, South Africa"
    };

    [Fact]
    public async Task CoverageCheck_ListsOnlyPackagesWhoseMappedProductIsOffered()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, F.Call(Palmas(F.OwnNetwork(skus: new[] { "OWC", "OWS" }))));

        var result = await h.Service.CheckAsync(PalmasRequest("8 Palmas Street"));

        Assert.True(result.IsSuccess, result.Message);
        var dto = result.Data!;
        Assert.True(dto.CoverageAvailable);
        Assert.Equal(CoverageMatchSource.OpenserveQualification, dto.MatchSource);
        Assert.Equal(new[] { h.OwcPackageId }, dto.AvailablePackages.Select(p => p.Id));
        Assert.NotNull(dto.QualificationReference);
        h.Gis.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CoverageCheck_UatCase_AddressVerificationRequired_NoFalseNoFibre_CustomerSafe()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, Uat(), verify: UatVerify());

        var result = await h.Service.CheckAsync(PalmasRequest("2 Palmas Street"));

        Assert.True(result.IsSuccess, result.Message);
        var dto = result.Data!;
        Assert.False(dto.CoverageAvailable);
        Assert.True(dto.AddressVerificationRequired);
        Assert.True(dto.AddressReviewRequired);
        Assert.Equal("AddressUnresolved", dto.FibreQualificationStatus);
        Assert.Empty(dto.AvailablePackages);
        Assert.Equal(OpenserveEligibilityAssessment.UnresolvedTitle, dto.FriendlyTitle);
        Assert.Equal(OpenserveEligibilityAssessment.UnresolvedMessage, dto.FriendlyMessage);
        Assert.DoesNotContain("isn't available", dto.FriendlyTitle + dto.FriendlyMessage);
        Assert.Equal(3, dto.NearbyOpenserveAddresses.Count);
        Assert.All(dto.NearbyOpenserveAddresses, n => Assert.False(n.MatchesYourAddress));
        var json = JsonSerializer.Serialize(dto);
        foreach (var leak in new[] { UatAmid, "80573005", "52782142", "api_key", H.FakeApiKey, "NORTH EASTERN", "REDACTED", "BLOCKED_" })
            Assert.DoesNotContain(leak, json);
        H.VerifyAmidQualifications(h.Client, Times.Never());
    }

    [Fact]
    public async Task CoverageCheck_AddressMatchesButNoFtth_FibreNotAvailable()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, Uat(), verify: UatVerify());

        var dto = (await h.Service.CheckAsync(PalmasRequest("8 Palmas Street"))).Data!;

        // 8 PALMAS matches the customer's own street number + street → its premises IS established.
        Assert.False(dto.CoverageAvailable);
        Assert.False(dto.AddressVerificationRequired);
        Assert.Equal("FtthUnavailable", dto.FibreQualificationStatus);
        Assert.Empty(dto.AvailablePackages);
        Assert.Equal("Fibre isn't available at your address yet.", dto.FriendlyTitle);
    }

    [Fact]
    public async Task CoverageCheckThenCheckout_ReusesTheQualification_OpenserveCalledOnce()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, F.Call(Palmas()));
        var qualification = H.Qualification(fixture.AppDbContext, h.Client);

        await h.Service.CheckAsync(PalmasRequest("8 Palmas Street"));
        var gate = await qualification.CheckFibreCheckoutAsync(new OpenserveLocationQuery(-25.866300m, 28.106700m, "8 Palmas Street", "Monavoni", "Centurion", "Gauteng"), h.OfcPackageId);

        Assert.True(gate.Applies);
        Assert.True(gate.Allowed, gate.Message);
        H.VerifyQualifyCalls(h.Client, Times.Once());
        var stored = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().Include(r => r.Products).SingleAsync(r => r.Id == gate.EvidenceId);
        Assert.Equal(OpenserveQualificationPurpose.CheckoutGate, stored.Purpose);
        Assert.Equal(OpenserveProductEligibility.Eligible, stored.ProductEligibility);
        Assert.Equal("OFC", stored.MappingSku);
        Assert.NotEmpty(stored.Products);
    }

    [Fact]
    public async Task CheckoutGate_QualificationUnavailable_FailsClosed()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, H.QualificationHttpFailure());

        var gate = await H.Qualification(fixture.AppDbContext, h.Client)
            .CheckFibreCheckoutAsync(new OpenserveLocationQuery(-25.866300m, 28.106700m, "8 Palmas Street", "Monavoni", "Centurion", "Gauteng"), h.OfcPackageId);

        Assert.False(gate.Allowed);
        Assert.Equal(ErrorCodes.UPSTREAM_UNAVAILABLE, gate.ErrorCode);
    }

    // ─── 8. The customer can't pay for an ineligible Fibre package ───

    [Fact]
    public async Task InitiatePayment_IneligibleFibrePackage_IsRefused_BeforeAnyGatewayOrIntent()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, Uat(), verify: UatVerify());
        var user = TestEntityFactory.CreateUser(fixture.AppDbContext, $"payer-{Guid.NewGuid():N}@example.com");
        await fixture.AppDbContext.SaveChangesAsync();
        var paystack = new Mock<IPaystackIntentInitiationService>(MockBehavior.Strict);
        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetMyEligibilityAsync(It.IsAny<ServicePackageType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<CustomerOrderEligibilityDto>.Success(new CustomerOrderEligibilityDto { CanCreateOrder = true }));
        var service = new OrderIntentService(fixture.AppDbContext, H.CurrentUser(user.Id), orders.Object, null!, null!, NullLogger<OrderIntentService>.Instance, paystack.Object,
            Mock.Of<IPayFastIntentInitiationService>(MockBehavior.Strict), Mock.Of<IOzowIntentInitiationService>(MockBehavior.Strict), Mock.Of<IPaymentApplierService>(MockBehavior.Strict),
            Mock.Of<ICustomerPaymentMandateService>(), Mock.Of<IHostEnvironment>(), Options.Create(new PayFastSettings()), Options.Create(new PaystackSettings()),
            Options.Create(new OzowSettings()), Mock.Of<IBillingDayOptionService>(), Options.Create(new BillingSettings()), H.Qualification(fixture.AppDbContext, h.Client));

        var result = await service.InitiateClientPaymentAsync(new InitiateOrderIntentPaymentRequestDto
        {
            ServicePackageId = h.OfcPackageId, FullName = "Jane Doe", Email = user.Email, PhoneNumber = "0821234567", AddressLine1 = "2 Palmas Street", Suburb = "Thorn Field Estate",
            City = "Centurion", Province = "Gauteng", PostalCode = "0157", Latitude = -25.866300m, Longitude = 28.106700m, PropertyType = PropertyType.House
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED, result.Code);
        Assert.Contains(OpenserveEligibilityAssessment.UnresolvedMessage, result.Message);
        Assert.DoesNotContain(UatAmid, result.Message);
        Assert.Empty(await fixture.DbContext.OrderIntents.AsNoTracking().ToListAsync());
        paystack.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateOrder_IneligibleFibrePackage_IsRefused_NoOrderCreated()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, F.Call(Palmas(F.OwnNetwork(skus: new[] { "OWC" }))));
        var user = TestEntityFactory.CreateUser(fixture.AppDbContext, $"buyer-{Guid.NewGuid():N}@example.com");
        await fixture.AppDbContext.SaveChangesAsync();

        var result = await OrderServiceFor(fixture, user.Id, H.Qualification(fixture.AppDbContext, h.Client)).CreateMineAsync(new CreateOrderRequestDto
        {
            ServicePackageId = h.OfcPackageId, FullName = "Jane Doe", Email = user.Email, PhoneNumber = "0821234567", AddressLine1 = "8 Palmas Street", Suburb = "Monavoni",
            City = "Centurion", Province = "Gauteng", PostalCode = "0157", Latitude = -25.866300m, Longitude = 28.106700m, PropertyType = PropertyType.House
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.OPENSERVE_PRODUCT_UNAVAILABLE, result.Code);
        Assert.Equal("This package isn't available at your address. Please choose one of the packages available at your address.", result.Message);
        Assert.Empty(await fixture.DbContext.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateOrder_EligibleFibrePackage_ReusesCheckoutEvidence_ForTheOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, F.Call(Palmas()));
        var user = TestEntityFactory.CreateUser(fixture.AppDbContext, $"buyer-{Guid.NewGuid():N}@example.com");
        await fixture.AppDbContext.SaveChangesAsync();

        var result = await OrderServiceFor(fixture, user.Id, H.Qualification(fixture.AppDbContext, h.Client)).CreateMineAsync(new CreateOrderRequestDto
        {
            ServicePackageId = h.OfcPackageId, FullName = "Jane Doe", Email = user.Email, PhoneNumber = "0821234567", AddressLine1 = "8 Palmas Street", Suburb = "Monavoni",
            City = "Centurion", Province = "Gauteng", PostalCode = "0157", Latitude = -25.866300m, Longitude = 28.106700m, PropertyType = PropertyType.House
        });

        Assert.True(result.IsSuccess, result.Message);
        var order = await fixture.DbContext.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(UatAmid, order.OpenserveAmId);
        var evidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync(r => r.Id == order.OpenserveQualificationResultId);
        Assert.Equal(order.Id, evidence.OrderId);
        Assert.Equal(OpenserveQualificationPurpose.CheckoutGate, evidence.Purpose);
        H.VerifyQualifyCalls(h.Client, Times.Once()); // the gate's call — the order didn't qualify again
    }

    private static OrderService OrderServiceFor(SqliteTestDbFixture fixture, Guid userId, IOpenserveQualificationService qualification) =>
        new(fixture.AppDbContext, Mock.Of<IAuditService>(), H.CurrentUser(userId), Mock.Of<INotificationService>(), Mock.Of<INetworkAccountService>(), Mock.Of<IInstallationService>(),
            Mock.Of<ICoverageCheckService>(), qualification, Options.Create(new PaymentSettings()), Options.Create(new BillingSettings()),
            NullLogger<OrderService>.Instance);

    [Fact]
    public async Task PaymentConversion_UsesTheCheckoutEvidence_WithoutCallingOpenserveAgain()
    {
        var (fx, _, intent, reference) = await SmartFuture.Tests.Billing.OrderIntentServiceConvertTests.SeedPaidIntentAsync(packageType: ServicePackageType.Fibre);
        await using var fixtureScope = fx;
        intent.Latitude = -25.866300m;
        intent.Longitude = 28.106700m;
        intent.AddressLine1 = "8 Palmas Street";
        intent.City = "Centurion";
        var evidence = OpenserveQualificationEvidence.Build(F.Call(Palmas()), OpenserveQualificationPurpose.CheckoutGate, -25.866300m, 28.106700m, null, null, DateTime.UtcNow);
        fx.AppDbContext.OpenserveQualificationResults.Add(evidence);
        intent.OpenserveQualificationResultId = evidence.Id;
        await fx.DbContext.SaveChangesAsync();
        var client = Client(new List<OpenserveCreateOrderCommand>());

        var converted = await SmartFuture.Tests.Billing.OrderIntentServiceConvertTests.BuildService(fx, SmartFuture.Tests.Billing.OrderIntentServiceConvertTests.LooseApplier(),
            openserveQualification: H.Qualification(fx.AppDbContext, client)).ConvertIntentPaymentToPaidOrderAsync(reference, paidAtUtc: null, gatewayTransactionId: "GW-TX-EVIDENCE");

        Assert.True(converted.IsSuccess, converted.Message);
        var order = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == converted.Data!.OrderId);
        Assert.Equal(UatAmid, order.OpenserveAmId);
        Assert.Equal(evidence.Id, order.OpenserveQualificationResultId);
        H.VerifyQualifyCalls(client, Times.Never());
    }

    // ─── 10. Security / Voice / LTE / Wireless are untouched ─────────

    [Theory]
    [InlineData(ServicePackageType.Security)]
    [InlineData(ServicePackageType.Voice)]
    [InlineData(ServicePackageType.LTE)]
    [InlineData(ServicePackageType.Wireless)]
    public async Task NonFibrePackages_CheckoutGateDoesNotApply_AndOpenserveIsNeverCalled(ServicePackageType type)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = TestEntityFactory.CreateServicePackage(fixture.AppDbContext, type: type, name: $"Non-fibre {type}");
        await fixture.AppDbContext.SaveChangesAsync();
        var client = Client(new List<OpenserveCreateOrderCommand>(), Uat());

        var gate = await H.Qualification(fixture.AppDbContext, client)
            .CheckFibreCheckoutAsync(new OpenserveLocationQuery(-25.8663m, 28.1067m, "2 Palmas Street", null, "Centurion", "Gauteng"), package.Id);

        Assert.False(gate.Applies);
        Assert.True(gate.Allowed);
        H.VerifyQualifyCalls(client, Times.Never());
    }

    [Fact]
    public async Task IntegrationDisabled_QualificationIsNotTheAuthority_LegacyBehaviourKept()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, Uat());
        var disabled = H.Qualification(fixture.AppDbContext, h.Client, H.Settings(enabled: false));

        Assert.False(disabled.CanQualify);
        var gate = await disabled.CheckFibreCheckoutAsync(new OpenserveLocationQuery(-25.8663m, 28.1067m, "2 Palmas Street", null, "Centurion", "Gauteng"), h.OfcPackageId);
        Assert.False(gate.Applies);
        Assert.Equal(OpenserveLocationStatus.NotAuthoritative, (await disabled.EvaluateLocationAsync(new OpenserveLocationQuery(-25.8663m, 28.1067m, null, null, null, null))).Status);
        H.VerifyQualifyCalls(h.Client, Times.Never());
    }

    // ─── 12. No credentials in logs / evidence ───────────────────────

    [Fact]
    public async Task CheckoutAndCoverageCalls_LogOnlyRedactedHeaders()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await CoverageAsync(fixture, Uat(), verify: UatVerify());

        await h.Service.CheckAsync(PalmasRequest("2 Palmas Street"));

        var log = await fixture.DbContext.OpenserveIntegrationLogs.AsNoTracking().SingleAsync(l => l.OperationType == OpenserveOperationType.ProductQualification);
        Assert.Contains("REDACTED", log.RequestHeadersJson);
        Assert.DoesNotContain(H.FakeApiKey, log.RequestHeadersJson ?? string.Empty);
        var evidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync();
        Assert.Equal(log.Id, evidence.AddressVerifyIntegrationLogId);
        Assert.Contains("8 PALMAS ST MONAVONI X 6 CENTURION", evidence.AddressCandidatesJson);
        foreach (var secret in new[] { H.FakeApiKey, "api_key", "REDACTED", "MessageID" })
            Assert.DoesNotContain(secret, evidence.AddressCandidatesJson);
        Assert.DoesNotContain(H.FakeApiKey, JsonSerializer.Serialize(evidence));
    }

    // ─── 13. Package mapping capacity ────────────────────────────────

    [Fact]
    public async Task Mapping_200MbpsPackageAsOfc100_CannotBeEnabled_AndAnEnabledOneBlocksSubmission()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var db = fixture.AppDbContext;
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: "SmartFuture Fibre 200/100");
        package.DownloadSpeedMbps = 200;
        await db.SaveChangesAsync();
        var service = new PackageOpenserveMappingService(db, NullLogger<PackageOpenserveMappingService>.Instance);

        var wrong = await service.CreateAsync(new SmartFuture.Application.Openserve.Dtos.CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id, OpenserveProductName = "Openserve Fibre Connect", Sku = "OFC", Capacity = "100", CapacityUom = "Mbps", IsEnabled = true
        });
        Assert.False(wrong.IsSuccess);
        Assert.Contains("does not match the package's 200 Mbps", wrong.Message);
        Assert.Contains("OFC 200 Mbps; OFC 200 Mbps Lite", wrong.Message); // documented options, never chosen for the business

        var right = await service.CreateAsync(new SmartFuture.Application.Openserve.Dtos.CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id, OpenserveProductName = "Openserve Fibre Connect", Sku = "OFC", Capacity = "200", CapacityUom = "Mbps", IsEnabled = true
        });
        Assert.True(right.IsSuccess, right.Message);

        // An already-enabled inconsistent mapping (saved before this check) blocks submission.
        var seeded = await SeedPalmasOrderAsync(fixture, addressLine1: "8 Palmas Street", sku: "OFC", capacity: "100", packageDownloadMbps: 200);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, F.Call(Palmas()));
        await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);
        Assert.Empty(sent);
        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveBlockedCodes.Mapping, record.LastFailureCode);
        Assert.Contains("200 Mbps", record.LastFailureMessage);
    }

    [Fact]
    public void Mapping_Ofc300MbpsLite_IsTheDocumentedOfc300Tier()
    {
        Assert.True(OpenserveProductCatalogue.IsOrderableAsNewSalesOrder("OFC", "300", "Mbps Lite"));
        Assert.False(OpenserveProductCatalogue.IsValidCombination("OFC", "300", "Mbps"));
        Assert.Null(OpenserveFibreEligibility.MappingCapacityConflict("300", "Mbps Lite", 300));
        Assert.NotNull(OpenserveFibreEligibility.MappingCapacityConflict("100", "Mbps", 200));
    }
}

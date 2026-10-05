using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Tests.Infrastructure;
using Xunit;
using H = SmartFuture.Tests.Openserve.OpenserveQualificationOrchestrationTests;

namespace SmartFuture.Tests.Openserve;

// MDU building/unit resolution is its own state, separate from the AMID.
// A valid AMID is always stored; when Openserve returns several building/unit
// rows and none matches the customer deterministically, Create Order is
// blocked (BLOCKED_BUILDING_UNIT) until Admin picks one of Openserve's own
// rows. PropertyType doesn't decide — the qualification response does, so a
// historical order with no PropertyType behaves the same way.
public class OpenserveBuildingUnitResolutionTests
{
    private const string Amid = H.Amid;

    private static List<OpenserveQualificationBuilding> ThreeUnits() => new()
    {
        new(Amid, "BLD-1", "B1", "F1", "1", "Oak Court", "Ground"),
        new(Amid, "BLD-2", "B1", "F1", "2", "Oak Court", "Ground"),
        new(Amid, "BLD-3", "B1", "F2", "3", "Oak Court", "First")
    };

    private static Task<OpenserveOrderFulfilmentDto> ViewAsync(SqliteTestDbFixture fixture, Mock<IOpenserveApiClient> client, Guid orderId, Guid? userId = null)
    {
        var qualification = H.Qualification(fixture.AppDbContext, client, userId: userId);
        return H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, client, qualification, userId: userId), qualification, userId: userId)
            .GetAsync(orderId).ContinueWith(t => t.Result.Data!);
    }

    // ─── allowed: no rows, one row, or a unique unit match ───────────

    [Theory]
    [InlineData(PropertyType.House)]
    [InlineData(null)] // historical order — the response decides, not an assumed House
    public async Task NoBuildingRows_AValidAmidIsEnough(PropertyType? propertyType)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, propertyType: propertyType);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = H.Client(sent, H.Qualified(buildings: new List<OpenserveQualificationBuilding>()));

        await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        var command = Assert.Single(sent);
        Assert.Equal(Amid, command.Amid);
        Assert.Null(command.BuildingNumId);
        var order = await H.OrderAsync(fixture, seeded.Order.Id);
        Assert.Equal(0, order.OpenserveBuildingCandidateCount);
    }

    [Theory]
    [InlineData(PropertyType.House)]
    [InlineData(PropertyType.Apartment)]
    [InlineData(null)]
    public async Task OneUnambiguousBuildingRow_IsPersisted_AndSubmitted(PropertyType? propertyType)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, propertyType: propertyType);
        var sent = new List<OpenserveCreateOrderCommand>();
        var single = new List<OpenserveQualificationBuilding> { new(Amid, "BLD-77", "B7", "F1", "7", "Lone House", null) };
        var client = H.Client(sent, H.Qualified(buildings: single));

        await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        var command = Assert.Single(sent);
        Assert.Equal("BLD-77", command.BuildingNumId);
        Assert.Equal("7", command.Unit);
        Assert.Equal("BLD-77", (await H.OrderAsync(fixture, seeded.Order.Id)).OpenserveBuildingNumId);
    }

    [Fact]
    public async Task MultipleRows_WithAUniqueUnitMatch_ArePersisted_AndSubmitted()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, propertyType: PropertyType.Apartment, buildingComplexName: "Oak Court", unitNumber: "Unit 2");
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = H.Client(sent, H.Qualified(buildings: ThreeUnits()));

        await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        var command = Assert.Single(sent);
        Assert.Equal("BLD-2", command.BuildingNumId);
        Assert.Equal("2", command.Unit);
        Assert.Equal("Oak Court", command.BuildingName);
    }

    // ─── blocked: several rows, no deterministic match ───────────────

    [Theory]
    [InlineData(PropertyType.Apartment, "99")]
    [InlineData(PropertyType.StudentResidence, null)]
    [InlineData(PropertyType.House, null)]  // any property where Openserve returns several rows
    [InlineData(null, null)]                // historical order: don't assume House
    public async Task MultipleRows_WithoutADeterministicMatch_StoreTheAmid_ButBlockSubmission(PropertyType? propertyType, string? unitNumber)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, propertyType: propertyType, buildingComplexName: "Oak Court", unitNumber: unitNumber);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = H.Client(sent, H.Qualified(buildings: ThreeUnits()));

        await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        Assert.Empty(sent); // never sent with a guessed BLD_NUM_ID
        var order = await H.OrderAsync(fixture, seeded.Order.Id);
        Assert.Equal(Amid, order.OpenserveAmId); // AMID stored regardless — separate state
        Assert.Null(order.OpenserveBuildingNumId);
        Assert.Equal(3, order.OpenserveBuildingCandidateCount);
        Assert.NotNull(order.OpenserveBuildingCandidatesJson);

        var record = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == seeded.Order.Id);
        Assert.Equal(OpenserveSubmissionFailureClass.Blocked, record.LastFailureClass);
        Assert.Equal(OpenserveBlockedCodes.BuildingUnit, record.LastFailureCode);
        Assert.Equal(OpenserveBuildingCandidates.MultipleUnitsReason, record.LastFailureMessage);
        Assert.Null(record.NextAutomaticRetryAtUtc);

        var view = await ViewAsync(fixture, client, seeded.Order.Id);
        Assert.Equal(OpenserveFulfilmentState.BlockedBuildingUnit, view.State);
        Assert.Equal("BLOCKED — BUILDING / UNIT DETAILS", view.StateLabel);
        Assert.Equal("Eligible", view.Qualification.Status); // Fibre/product eligible — the unit is a separate blocker
        Assert.Equal(Amid, view.Qualification.AmId);
        Assert.Equal("NeedsResolution", view.Qualification.BuildingResolution);
        Assert.Equal(propertyType?.ToString(), view.Qualification.PropertyType);
        Assert.Equal(3, view.Qualification.BuildingCandidates.Count);
        Assert.True(view.Qualification.CanSelectBuilding, view.Qualification.CannotSelectBuildingReason);
        Assert.False(view.ManualSubmission.Allowed);
        Assert.Equal(OpenserveBuildingCandidates.MultipleUnitsReason, view.ManualSubmission.Reason);
        Assert.False(view.AutomaticRetry.Allowed);
    }

    [Fact]
    public async Task BlockedBuildingUnit_IsNeverRetriedOrSweptAutomatically()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, propertyType: PropertyType.Apartment);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = H.Client(sent, H.Qualified(buildings: ThreeUnits()));
        var submission = H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client));
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        var recovery = new OpenserveSubmissionRecoveryService(fixture.AppDbContext, submission, H.Config(H.Settings()), NullLogger<OpenserveSubmissionRecoveryService>.Instance);
        await recovery.RunRetryPassAsync();
        await recovery.RunSafetySweepAsync();

        Assert.Empty(sent);
    }

    // ─── Admin resolution ────────────────────────────────────────────

    [Fact]
    public async Task AdminSelectsAUnitOpenserveReturned_ItIsSavedAndAudited_NothingIsSent_ThenRetrySendsIt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, propertyType: PropertyType.Apartment, buildingComplexName: "Oak Court", unitNumber: "3");
        var admin = TestEntityFactory.CreateUser(fixture.AppDbContext, $"admin-{Guid.NewGuid():N}@example.com", "Naledi", "Admin");
        await fixture.AppDbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        // Openserve's rows don't carry the customer's unit number in NUM → no deterministic match.
        var rows = new List<OpenserveQualificationBuilding>
        {
            new(Amid, "BLD-A", "B1", "F1", "1A", "Oak Court North", "Ground"),
            new(Amid, "BLD-B", "B2", "F2", "3B", "Oak Court South", "First")
        };
        var client = H.Client(sent, H.Qualified(buildings: rows));
        var qualification = H.Qualification(fixture.AppDbContext, client, userId: admin.Id);
        var submission = H.Submission(fixture.AppDbContext, client, qualification, userId: admin.Id);
        var fulfilment = H.Fulfilment(fixture.AppDbContext, submission, qualification, userId: admin.Id);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);
        Assert.Empty(sent);

        var selected = await fulfilment.SelectBuildingUnitAsync(seeded.Order.Id, "BLD-B");

        Assert.True(selected.IsSuccess, selected.Message);
        Assert.Empty(sent); // choosing the unit never submits
        var order = await H.OrderAsync(fixture, seeded.Order.Id);
        Assert.Equal("BLD-B", order.OpenserveBuildingNumId);
        Assert.Equal("3B", order.OpenserveUnit);
        Assert.Equal("First", order.OpenserveFloor);
        Assert.Equal("Oak Court South", order.OpenserveBuildingName);
        Assert.Equal(Amid, order.OpenserveAmId);

        var audit = await fixture.DbContext.AuditLogs.AsNoTracking().SingleAsync(a => a.ActionType == AuditActionType.OpenserveBuildingUnitSelected && a.EntityId == seeded.Order.Id);
        Assert.Equal(admin.Id, audit.ActorUserId);
        Assert.Contains("BLD-B", audit.MetadataJson);

        var view = selected.Data!;
        Assert.Equal("Resolved", view.Qualification.BuildingResolution);
        Assert.Equal("Naledi Admin", view.Qualification.BuildingSelectedBy);
        Assert.NotNull(view.Qualification.BuildingSelectedAtUtc);
        Assert.True(view.Qualification.BuildingCandidates.Single(c => c.BldNumId == "BLD-B").IsSelected);
        Assert.True(view.ManualSubmission.Allowed, view.ManualSubmission.Reason);
        Assert.Equal("Retry", view.ManualSubmission.Action);
        Assert.Contains("now looks resolved", view.StateReason);
        Assert.Contains(view.Activity, a => a.Title == "Building/unit selected by Naledi Admin — Unit 3B (BLD-B)");

        var retried = await fulfilment.SubmitAsync(seeded.Order.Id, confirmOutcomeUnknown: false);
        Assert.True(retried.Data!.ForwardedToOpenserve);
        var command = Assert.Single(sent);
        Assert.Equal("BLD-B", command.BuildingNumId);
        Assert.Equal("3B", command.Unit);
        Assert.Equal("First", command.Floor);
        Assert.Equal("Oak Court South", command.BuildingName);
    }

    [Theory]
    [InlineData("BLD-999")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AdminCannotChooseABuildingUnitOpenserveDidNotReturn(string? bldNumId)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, propertyType: PropertyType.Apartment);
        var client = H.Client(new List<OpenserveCreateOrderCommand>(), H.Qualified(buildings: ThreeUnits()));
        var qualification = H.Qualification(fixture.AppDbContext, client);
        var submission = H.Submission(fixture.AppDbContext, client, qualification);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        var result = await H.Fulfilment(fixture.AppDbContext, submission, qualification).SelectBuildingUnitAsync(seeded.Order.Id, bldNumId);

        Assert.False(result.IsSuccess);
        Assert.Null((await H.OrderAsync(fixture, seeded.Order.Id)).OpenserveBuildingNumId);
    }

    [Fact]
    public async Task BuildingUnitCannotChange_OnceOpenserveHasTheOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, propertyType: PropertyType.Apartment, buildingComplexName: "Oak Court", unitNumber: "2");
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = H.Client(sent, H.Qualified(buildings: ThreeUnits()));
        var qualification = H.Qualification(fixture.AppDbContext, client);
        var submission = H.Submission(fixture.AppDbContext, client, qualification);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);
        Assert.Single(sent);

        var result = await H.Fulfilment(fixture.AppDbContext, submission, qualification).SelectBuildingUnitAsync(seeded.Order.Id, "BLD-3");

        Assert.False(result.IsSuccess);
        Assert.Equal("BLD-2", (await H.OrderAsync(fixture, seeded.Order.Id)).OpenserveBuildingNumId);
    }

    // ─── historical orders qualified before rows were stored ─────────

    [Fact]
    public async Task LegacyMultiUnitOrder_IsBlocked_AndAdminCanReloadTheRowsByAmid_ThenChoose()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, amid: Amid, propertyType: null);
        await fixture.DbContext.Orders.Where(o => o.Id == seeded.Order.Id).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.OpenserveQualifiedAtUtc, DateTime.UtcNow.AddDays(-2))
            .SetProperty(o => o.OpenserveQualificationFailureReason,
                "AMID captured, but 3 building/unit matches were returned and the order has no unit number to match — building details left blank pending unit confirmation."));
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = H.Client(sent, H.Qualified(buildings: ThreeUnits()));
        var qualification = H.Qualification(fixture.AppDbContext, client);
        var submission = H.Submission(fixture.AppDbContext, client, qualification);
        var fulfilment = H.Fulfilment(fixture.AppDbContext, submission, qualification);

        // Before submission: the legacy order is shown as blocked and its rows can be reloaded by AMID.
        var legacy = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        Assert.False(legacy.Qualification.CanSelectBuilding);
        Assert.Contains("Reload them", legacy.Qualification.CannotSelectBuildingReason);
        Assert.True(legacy.Qualification.CanRefreshBuildingCandidates, legacy.Qualification.CannotRefreshBuildingCandidatesReason);

        var refreshed = await fulfilment.RefreshBuildingCandidatesAsync(seeded.Order.Id);

        Assert.True(refreshed.IsSuccess, refreshed.Message);
        client.Verify(c => c.QualifyAsync(It.Is<OpenserveQualificationQuery>(q => q.Amid == Amid && q.BuildingInfo), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(3, refreshed.Data!.Qualification.BuildingCandidates.Count);
        Assert.Equal(Amid, (await H.OrderAsync(fixture, seeded.Order.Id)).OpenserveAmId);
        // The AMID query also records the evidence (Fibre/products) the legacy order lacked.
        Assert.Equal("Eligible", refreshed.Data.Qualification.Status);

        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);
        Assert.Empty(sent);
        H.VerifyQualifyCalls(client, Times.Once()); // evidence now current — submission doesn't re-qualify
        Assert.Equal(OpenserveFulfilmentState.BlockedBuildingUnit, (await fulfilment.GetAsync(seeded.Order.Id)).Data!.State);

        var selected = await fulfilment.SelectBuildingUnitAsync(seeded.Order.Id, "BLD-1");
        Assert.True(selected.Data!.ManualSubmission.Allowed, selected.Data.ManualSubmission.Reason);
    }

    [Fact]
    public async Task ReloadingRows_NeverChangesTheAmid()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, amid: Amid);
        var client = H.Client(new List<OpenserveCreateOrderCommand>(), H.Qualified(amid: "99999999", buildings: ThreeUnits()));
        var qualification = H.Qualification(fixture.AppDbContext, client);

        var result = await H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, client, qualification), qualification).RefreshBuildingCandidatesAsync(seeded.Order.Id);

        Assert.False(result.IsSuccess);
        var order = await H.OrderAsync(fixture, seeded.Order.Id);
        Assert.Equal(Amid, order.OpenserveAmId);
        Assert.Null(order.OpenserveBuildingCandidatesJson);
    }

    // ─── access + secrets ────────────────────────────────────────────

    [Fact]
    public void BuildingUnitEndpoints_AreAdminOnly()
    {
        var controller = typeof(SmartFuture.API.Controllers.OpenserveOrdersController);
        Assert.Equal(SmartFuture.Shared.Constants.AuthorizationPolicies.RequireAdmin, controller.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()!.Policy);
        foreach (var name in new[] { "SelectBuildingUnit", "RefreshBuildingCandidates" })
        {
            var action = controller.GetMethod(name);
            Assert.NotNull(action);
            Assert.Null(action!.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        }
    }

    [Fact]
    public async Task CandidateRows_CarryNoCredentials()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, propertyType: PropertyType.Apartment);
        var client = H.Client(new List<OpenserveCreateOrderCommand>(), H.Qualified(buildings: ThreeUnits()));
        await H.Submission(fixture.AppDbContext, client, H.Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        var json = JsonSerializer.Serialize(await ViewAsync(fixture, client, seeded.Order.Id));
        var stored = (await H.OrderAsync(fixture, seeded.Order.Id)).OpenserveBuildingCandidatesJson!;

        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", stored, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BLD-3", stored);
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Tests.Billing;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// UAT SF-20261004-DB06F161: a payment-first Fibre order reached Openserve
// submission with no AMID because Product Qualification never ran on that
// path. These pin the fix: one shared qualification routine, run before
// submission on every path, self-healing in the coordinator, an Admin
// "Run Product Qualification" that never sends, and no guessed values.
public class OpenserveQualificationOrchestrationTests
{
    internal const string FakeApiKey = "fake-qualify-api-key-3b81e0";
    internal const string Amid = "50782408";

    // ─── harness ────────────────────────────────────────────────────

    internal static OpenserveFulfilmentSettings Settings(bool enabled = true) => new()
    {
        Enabled = enabled, BaseUrl = "https://stapitrx.openserve.co.za", ApiKey = FakeApiKey, WsIspCode = "ws-marut", IspIdentifier = "WS MARUT",
        SenderId = "SMARTFUTURE", ReplyToAddress = "https://stapitrx.openserve.co.za/ws-marut/productordercallback"
    };

    internal static IOpenserveRuntimeConfigProvider Config(OpenserveFulfilmentSettings settings)
    {
        var m = new Mock<IOpenserveRuntimeConfigProvider>();
        m.Setup(x => x.Current).Returns(settings);
        return m.Object;
    }

    internal static ICurrentUserService CurrentUser(Guid? userId)
    {
        var m = new Mock<ICurrentUserService>();
        m.SetupGet(c => c.UserId).Returns(userId);
        return m.Object;
    }

    /// <summary>A full answer for the seeded order's own address ("61 OAK AVE … RANDBURG"), Fibre Working with every mapped SKU.</summary>
    internal static OpenserveApiCallResult<OpenserveQualificationOutcome> Qualified(string? amid = Amid, IReadOnlyList<OpenserveQualificationBuilding>? buildings = null) =>
        OpenserveEvidenceFixtures.Call(OpenserveEvidenceFixtures.Facts(amid, suburb: null, town: "RANDBURG", buildings: buildings));

    internal static OpenserveApiCallResult<OpenserveQualificationOutcome> QualificationHttpFailure() =>
        OpenserveApiCallResult<OpenserveQualificationOutcome>.Failure(Guid.NewGuid().ToString(), "GET", "https://stapitrx.openserve.co.za/ws-marut/productqualification", 503, "", "Service Unavailable",
            "ServiceUnavailable", "Openserve returned HTTP 503.", "{\"api_key\":\"***\"}");

    /// <summary>One mock for both Openserve calls: QualifyAsync returns the scripted results in order; CreateOrderAsync accepts and records.</summary>
    internal static Mock<IOpenserveApiClient> Client(List<OpenserveCreateOrderCommand> sent, params OpenserveApiCallResult<OpenserveQualificationOutcome>[] qualifications)
    {
        var queue = new Queue<OpenserveApiCallResult<OpenserveQualificationOutcome>>(qualifications.Length > 0 ? qualifications : new[] { Qualified() });
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        client.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OpenserveCreateOrderCommand command, CancellationToken _) =>
            {
                sent.Add(command);
                return OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Success(Guid.NewGuid().ToString(), "POST", "https://stapitrx.openserve.co.za/ws-marut/productorder", 200, "{}", "{}",
                    new OpenserveCreateOrderOutcome("1742148", "Validated", "Order received for processing. Order Id = 1742148. State = Validated"));
            });
        return client;
    }

    internal static OpenserveQualificationService Qualification(IAppDbContext db, Mock<IOpenserveApiClient> client, OpenserveFulfilmentSettings? settings = null, Guid? userId = null) =>
        new(db, client.Object, Config(settings ?? Settings()), NullLogger<OpenserveQualificationService>.Instance, new AuditService(db, NullLogger<AuditService>.Instance), CurrentUser(userId));

    internal static OpenserveOrderSubmissionService Submission(IAppDbContext db, Mock<IOpenserveApiClient> client, IOpenserveQualificationService? qualification, OpenserveFulfilmentSettings? settings = null,
        Guid? userId = null) =>
        new(db, client.Object, new DefaultOpenserveSubscriberReferenceGenerator(), Config(settings ?? Settings()), new AuditService(db, NullLogger<AuditService>.Instance), CurrentUser(userId),
            NullLogger<OpenserveOrderSubmissionService>.Instance, qualification);

    internal static OpenserveOrderFulfilmentService Fulfilment(IAppDbContext db, IOpenserveOrderSubmissionService submission, IOpenserveQualificationService qualification,
        OpenserveFulfilmentSettings? settings = null, Guid? userId = null) =>
        new(db, submission, Config(settings ?? Settings()), new AuditService(db, NullLogger<AuditService>.Instance), CurrentUser(userId), qualification,
            NullLogger<OpenserveOrderFulfilmentService>.Instance);

    internal sealed record Seeded(Order Order, NetworkAccount Account);

    internal static async Task<Seeded> SeedAsync(SqliteTestDbFixture fixture, bool withCoordinates = true, string? amid = null, ServicePackageType type = ServicePackageType.Fibre,
        PropertyType? propertyType = PropertyType.House, string? buildingComplexName = null, string? unitNumber = null)
    {
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"qualify-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: type, name: $"SmartFuture Fibre 100 {Guid.NewGuid():N}");
        var order = TestEntityFactory.CreateOrder(db, user, package, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        order.AddressLine1 = "61 Oak Ave";
        order.City = "Randburg";
        order.FullName = "Jane Doe";
        order.PhoneNumber = "0821234567";
        order.Email = user.Email;
        order.Latitude = withCoordinates ? -26.095950m : null;
        order.Longitude = withCoordinates ? 27.927632m : null;
        order.OpenserveAmId = amid;
        order.PropertyType = propertyType;
        order.BuildingComplexName = buildingComplexName;
        order.UnitNumber = unitNumber;
        await db.SaveChangesAsync();

        db.PackageOpenserveMappings.Add(new PackageOpenserveMapping
        {
            Id = Guid.NewGuid(), ServicePackageId = package.Id, OpenserveProductName = "Openserve Web Connect", Sku = "OWC", Capacity = "20", CapacityUom = "Mbps",
            IsEnabled = true, CreatedAtUtc = DateTime.UtcNow
        });
        var account = TestEntityFactory.CreateNetworkAccount(db, order, status: NetworkAccountStatus.Pending, packageType: type);
        await db.SaveChangesAsync();
        return new Seeded(order, account);
    }

    internal static Task<Order> OrderAsync(SqliteTestDbFixture fixture, Guid orderId) => fixture.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);

    internal static void VerifyQualifyCalls(Mock<IOpenserveApiClient> client, Times times) =>
        client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), times);

    // ─── 3 / 4 / 5 / 14. Payment-first order: qualified, then submitted with that AMID ─

    [Fact]
    public async Task PaymentFirstFibreOrder_IsQualifiedOnConversion_ThenSubmittedWithThatAmid_WithoutRequalifying()
    {
        var (fx, _, intent, reference) = await OrderIntentServiceConvertTests.SeedPaidIntentAsync(packageType: ServicePackageType.Fibre);
        await using var fixtureScope = fx;
        intent.Latitude = -26.095950m;
        intent.Longitude = 27.927632m;
        intent.AddressLine1 = "61 Oak Ave"; // the address Openserve resolves for these coordinates
        intent.City = "Randburg";
        await fx.DbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, Qualified());
        var qualification = Qualification(fx.AppDbContext, client);

        var converted = await OrderIntentServiceConvertTests.BuildService(fx, OrderIntentServiceConvertTests.LooseApplier(), openserveQualification: qualification)
            .ConvertIntentPaymentToPaidOrderAsync(reference, paidAtUtc: null, gatewayTransactionId: "GW-TX-QUAL-1");

        Assert.True(converted.IsSuccess, converted.Message);
        var order = await fx.DbContext.Orders.SingleAsync(o => o.Id == converted.Data!.OrderId);
        Assert.Equal(Amid, order.OpenserveAmId);
        Assert.NotNull(order.OpenserveQualifiedAtUtc);

        // The (mocked) applier would reserve the network account and fire the
        // automatic trigger — do exactly that against the real coordinator.
        fx.AppDbContext.PackageOpenserveMappings.Add(new PackageOpenserveMapping
        {
            Id = Guid.NewGuid(), ServicePackageId = order.ServicePackageId!.Value, OpenserveProductName = "Openserve Web Connect", Sku = "OWC", Capacity = "20", CapacityUom = "Mbps",
            IsEnabled = true, CreatedAtUtc = DateTime.UtcNow
        });
        var account = TestEntityFactory.CreateNetworkAccount(fx.AppDbContext, order, status: NetworkAccountStatus.Pending);
        await fx.AppDbContext.SaveChangesAsync();
        await Submission(fx.AppDbContext, client, qualification).TrySubmitForOrderAsync(order.Id, account.Id);

        var command = Assert.Single(sent);
        Assert.Equal(Amid, command.Amid);
        VerifyQualifyCalls(client, Times.Once()); // the stored AMID is reused, never re-qualified
    }

    // ─── 6. Self-heal: missing AMID + coordinates → qualify first ────

    [Fact]
    public async Task MissingAmidWithCoordinates_SubmissionRunsQualificationFirst_ThenSends()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, Qualified());

        await Submission(fixture.AppDbContext, client, Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        VerifyQualifyCalls(client, Times.Once());
        var command = Assert.Single(sent);
        Assert.Equal(Amid, command.Amid);
        Assert.Equal(Amid, (await OrderAsync(fixture, seeded.Order.Id)).OpenserveAmId);
        var audit = await fixture.DbContext.AuditLogs.AsNoTracking().SingleAsync(a => a.ActionType == AuditActionType.OpenserveOrderQualificationRun && a.EntityId == seeded.Order.Id);
        Assert.Contains(nameof(OpenserveQualificationTrigger.SubmissionSelfHeal), audit.MetadataJson);
    }

    // ─── 7. Qualification failure blocks safely, with the reason ─────

    [Theory]
    [InlineData(true, "Openserve returned HTTP 503.")]
    [InlineData(false, "Openserve returned no AMID for this address.")]
    public async Task QualificationFailure_BlocksSubmissionSafely_AndRecordsWhy(bool httpFailure, string expectedReason)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, httpFailure ? QualificationHttpFailure() : Qualified(amid: null));

        await Submission(fixture.AppDbContext, client, Qualification(fixture.AppDbContext, client)).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        Assert.Empty(sent);
        var order = await OrderAsync(fixture, seeded.Order.Id);
        Assert.Null(order.OpenserveAmId);
        Assert.NotNull(order.OpenserveQualifiedAtUtc);
        Assert.Equal(expectedReason, order.OpenserveQualificationFailureReason);
        var record = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == seeded.Order.Id);
        Assert.Equal(OpenserveSubmissionFailureClass.Blocked, record.LastFailureClass);
        Assert.Equal(OpenserveBlockedCodes.Amid, record.LastFailureCode);
        Assert.Contains("Product Qualification ran at", record.LastFailureMessage);
        Assert.Contains(expectedReason, record.LastFailureMessage);
    }

    // ─── 8. Missing coordinates never call Openserve ─────────────────

    [Fact]
    public async Task MissingCoordinates_NeverCallProductQualification_AndSayWhy()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, withCoordinates: false);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, Qualified());
        var qualification = Qualification(fixture.AppDbContext, client);
        var submission = Submission(fixture.AppDbContext, client, qualification);
        var fulfilment = Fulfilment(fixture.AppDbContext, submission, qualification);

        var before = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        var adminRun = await fulfilment.RunQualificationAsync(seeded.Order.Id);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);

        VerifyQualifyCalls(client, Times.Never());
        Assert.Empty(sent);
        Assert.False(before.Qualification.CoordinatesAvailable);
        Assert.False(before.Qualification.CanRun);
        Assert.Equal(OpenserveQualificationService.MissingCoordinatesReason, before.Qualification.CannotRunReason);
        Assert.Contains("installation coordinates are missing", before.ManualSubmission.Reason);
        Assert.False(adminRun.IsSuccess);
        var record = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == seeded.Order.Id);
        Assert.Equal(OpenserveBlockedCodes.Amid, record.LastFailureCode);
        Assert.Contains("installation coordinates are missing", record.LastFailureMessage);
    }

    [Fact]
    public async Task PlaceholderZeroCoordinates_AreNotUsable()
    {
        Assert.False(OpenserveQualificationService.AreUsable(0m, 0m));
        Assert.False(OpenserveQualificationService.AreUsable(null, 27.9m));
        Assert.False(OpenserveQualificationService.AreUsable(-126m, 27.9m));
        Assert.True(OpenserveQualificationService.AreUsable(-26.095950m, 27.927632m));
        await Task.CompletedTask;
    }

    // ─── 9 / 10 / 11. Admin recovery for an already-blocked order ────

    [Fact]
    public async Task AdminRunQualification_RecoversABlockedOrder_WithoutSendingIt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var admin = TestEntityFactory.CreateUser(fixture.AppDbContext, $"admin-{Guid.NewGuid():N}@example.com", "Lerato", "Admin");
        await fixture.AppDbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, Qualified());

        // The order as the previous deploy left it: submitted with no qualification → BLOCKED_AMID.
        await Submission(fixture.AppDbContext, client, qualification: null).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);
        var qualification = Qualification(fixture.AppDbContext, client, userId: admin.Id);
        var submission = Submission(fixture.AppDbContext, client, qualification, userId: admin.Id);
        var fulfilment = Fulfilment(fixture.AppDbContext, submission, qualification, userId: admin.Id);

        var blocked = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        Assert.Equal(OpenserveFulfilmentState.BlockedOrderData, blocked.State);
        Assert.Equal("NotRun", blocked.Qualification.Status);
        Assert.True(blocked.Qualification.CanRun, blocked.Qualification.CannotRunReason);
        Assert.False(blocked.ManualSubmission.Allowed);

        var run = await fulfilment.RunQualificationAsync(seeded.Order.Id);

        Assert.True(run.IsSuccess, run.Message);
        Assert.Contains($"AMID {Amid}", run.Message);
        var view = run.Data!;
        Assert.Equal("Eligible", view.Qualification.Status); // AMID + Fibre + the mapped product + address confirmed
        Assert.Equal(Amid, view.Qualification.AmId);
        Assert.Equal(Amid, view.AmId);
        Assert.True(view.ManualSubmission.Allowed, view.ManualSubmission.Reason);
        Assert.Equal("Retry", view.ManualSubmission.Action);
        Assert.Contains("now looks resolved", view.StateReason);
        Assert.Empty(sent); // qualification never POSTs productOrder
        Assert.False(view.ForwardedToOpenserve);
        Assert.Contains(view.Activity, a => a.Title == "Product Qualification by Lerato Admin — AMID 50782408, eligible");

        // Admin then explicitly retries — the stored AMID is what goes to Openserve.
        var retried = await fulfilment.SubmitAsync(seeded.Order.Id, confirmOutcomeUnknown: false);
        Assert.True(retried.Data!.ForwardedToOpenserve);
        Assert.Equal(Amid, Assert.Single(sent).Amid);
        VerifyQualifyCalls(client, Times.Once());
    }

    // ─── 12 / 13. MDU: AMID stored, building/unit never guessed ──────

    [Fact]
    public async Task SeveralBuildingCandidates_StoreTheAmid_ButNeverGuessTheBuildingNumId()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, propertyType: PropertyType.Apartment, buildingComplexName: "Oak Court", unitNumber: "99");
        var buildings = new List<OpenserveQualificationBuilding>
        {
            new(Amid, "BLD-1", "B1", "F1", "1", "Oak Court", "Ground"),
            new(Amid, "BLD-2", "B1", "F1", "2", "Oak Court", "Ground"),
            new(Amid, "BLD-3", "B1", "F2", "3", "Oak Court", "First")
        };
        var client = Client(new List<OpenserveCreateOrderCommand>(), Qualified(buildings: buildings));
        var qualification = Qualification(fixture.AppDbContext, client);

        var result = await qualification.QualifyAndPersistAsync(seeded.Order.Id, OpenserveQualificationTrigger.AdminManual, ignoreCooldown: true);
        var view = (await Fulfilment(fixture.AppDbContext, Submission(fixture.AppDbContext, client, qualification), qualification).GetAsync(seeded.Order.Id)).Data!;

        Assert.Equal(OpenserveQualificationRunStatus.Qualified, result.Status);
        var order = await OrderAsync(fixture, seeded.Order.Id);
        Assert.Equal(Amid, order.OpenserveAmId);
        Assert.Null(order.OpenserveBuildingNumId);
        Assert.Null(order.OpenserveUnit);
        Assert.Equal("NeedsResolution", view.Qualification.BuildingResolution);
        Assert.Contains("3 building/unit matches", view.Qualification.BuildingNote);
        Assert.Equal("Eligible", view.Qualification.Status); // Fibre/product eligible — the building/unit is a separate blocker
    }

    [Fact]
    public async Task AUniqueUnitMatch_ResolvesTheBuildingNumId()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, propertyType: PropertyType.Apartment, buildingComplexName: "Oak Court", unitNumber: "2");
        var buildings = new List<OpenserveQualificationBuilding>
        {
            new(Amid, "BLD-1", "B1", "F1", "1", "Oak Court", "Ground"),
            new(Amid, "BLD-2", "B1", "F1", "2", "Oak Court", "Ground")
        };
        var client = Client(new List<OpenserveCreateOrderCommand>(), Qualified(buildings: buildings));

        await Qualification(fixture.AppDbContext, client).QualifyAndPersistAsync(seeded.Order.Id, OpenserveQualificationTrigger.AdminManual, ignoreCooldown: true);

        var order = await OrderAsync(fixture, seeded.Order.Id);
        Assert.Equal(Amid, order.OpenserveAmId);
        Assert.Equal("BLD-2", order.OpenserveBuildingNumId);
        Assert.Equal("2", order.OpenserveUnit);
    }

    // ─── 14. Existing AMID is never re-qualified ─────────────────────

    [Fact]
    public async Task ExistingAmidWithEvidence_IsNeverRequalifiedAutomatically_AndNotOnceWithOpenserve()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, amid: "1000497");
        var tracked = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == seeded.Order.Id);
        await OpenserveEvidenceFixtures.SeedEligibleEvidenceAsync(fixture.AppDbContext, tracked);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, Qualified());
        var qualification = Qualification(fixture.AppDbContext, client);
        var submission = Submission(fixture.AppDbContext, client, qualification);

        var selfHeal = await qualification.QualifyAndPersistAsync(seeded.Order.Id, OpenserveQualificationTrigger.SubmissionSelfHeal, ignoreCooldown: true);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);
        var adminRun = await Fulfilment(fixture.AppDbContext, submission, qualification).RunQualificationAsync(seeded.Order.Id);

        VerifyQualifyCalls(client, Times.Never());
        Assert.Equal(OpenserveQualificationRunStatus.Skipped, selfHeal.Status);
        Assert.Equal("1000497", Assert.Single(sent).Amid);
        // Once Openserve has the order, even Admin can't re-qualify it.
        Assert.False(adminRun.IsSuccess);
        Assert.Contains("Already with Openserve", adminRun.Message);
    }

    // ─── Cooldown: no hammering after a recent failure ───────────────

    [Fact]
    public async Task RecentFailedQualification_IsNotRepeatedBySubmission_ButAdminCanRunItAgain()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        await fixture.DbContext.Orders.Where(o => o.Id == seeded.Order.Id).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.OpenserveQualifiedAtUtc, DateTime.UtcNow.AddMinutes(-5))
            .SetProperty(o => o.OpenserveQualificationFailureReason, "Openserve returned HTTP 503."));
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, Qualified());
        var qualification = Qualification(fixture.AppDbContext, client);
        var submission = Submission(fixture.AppDbContext, client, qualification);

        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account.Id);
        VerifyQualifyCalls(client, Times.Never());
        Assert.Empty(sent);
        var record = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == seeded.Order.Id);
        Assert.Contains("Openserve returned HTTP 503.", record.LastFailureMessage);

        var adminRun = await Fulfilment(fixture.AppDbContext, submission, qualification).RunQualificationAsync(seeded.Order.Id);
        Assert.True(adminRun.IsSuccess, adminRun.Message);
        VerifyQualifyCalls(client, Times.Once());
        Assert.Equal(Amid, adminRun.Data!.Qualification.AmId);
        Assert.Empty(sent);
    }

    // ─── 15. Non-Fibre untouched ─────────────────────────────────────

    [Fact]
    public async Task NonFibreOrders_AreNeverQualified()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, type: ServicePackageType.Security);
        var client = Client(new List<OpenserveCreateOrderCommand>(), Qualified());
        var qualification = Qualification(fixture.AppDbContext, client);

        var direct = await qualification.QualifyAndPersistAsync(seeded.Order.Id, OpenserveQualificationTrigger.AdminManual, ignoreCooldown: true);
        var adminRun = await Fulfilment(fixture.AppDbContext, Submission(fixture.AppDbContext, client, qualification), qualification).RunQualificationAsync(seeded.Order.Id);

        VerifyQualifyCalls(client, Times.Never());
        Assert.Equal(OpenserveQualificationRunStatus.Skipped, direct.Status);
        Assert.False(adminRun.IsSuccess);
        Assert.Null((await OrderAsync(fixture, seeded.Order.Id)).OpenserveQualifiedAtUtc);
    }

    // ─── 16. Access + secrets ────────────────────────────────────────

    [Fact]
    public void RunQualificationEndpoint_IsAdminOnly()
    {
        var controller = typeof(SmartFuture.API.Controllers.OpenserveOrdersController);
        Assert.Equal(SmartFuture.Shared.Constants.AuthorizationPolicies.RequireAdmin, controller.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()!.Policy);
        var action = controller.GetMethod("RunQualification");
        Assert.NotNull(action);
        Assert.Null(action!.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
    }

    [Fact]
    public async Task NoCredentials_InTheQualificationViewOrItsAuditTrail()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var client = Client(new List<OpenserveCreateOrderCommand>(), Qualified());
        var qualification = Qualification(fixture.AppDbContext, client);
        var fulfilment = Fulfilment(fixture.AppDbContext, Submission(fixture.AppDbContext, client, qualification), qualification);

        var view = (await fulfilment.RunQualificationAsync(seeded.Order.Id)).Data!;
        var json = JsonSerializer.Serialize(view);

        Assert.DoesNotContain(FakeApiKey, json);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
        var forbidden = new Regex("api.?key|secret|password|token|header|requestbody|responsebody", RegexOptions.IgnoreCase);
        Assert.DoesNotContain(typeof(OpenserveQualificationStateDto).GetProperties(), p => forbidden.IsMatch(p.Name));
        var audits = await fixture.DbContext.AuditLogs.AsNoTracking().Where(a => a.EntityId == seeded.Order.Id).Select(a => (a.Summary ?? "") + (a.MetadataJson ?? "")).ToListAsync();
        Assert.NotEmpty(audits);
        Assert.DoesNotContain(audits, a => a.Contains(FakeApiKey) || a.Contains("api_key"));
    }
}

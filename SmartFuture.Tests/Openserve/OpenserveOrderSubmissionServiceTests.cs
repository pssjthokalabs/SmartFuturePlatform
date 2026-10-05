using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Openserve;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Brief §Priority-1/TEST REQUIREMENTS: eligible order submits, unmapped
// package does not, duplicate trigger does not double-submit, transient
// failure visible for retry, External Reference / Subscriber Reference
// strategy.
public class OpenserveOrderSubmissionServiceTests
{
    private static OpenserveFulfilmentSettings EnabledSettings() => new()
    {
        Enabled = true, BaseUrl = "https://stapitrx.openserve.co.za", ApiKey = "key", WsIspCode = "ws-marut", IspIdentifier = "WS MARUT",
        SenderId = "SMARTFUTURE", ReplyToAddress = "https://stapitrx.openserve.co.za/ws-marut/productordercallback"
    };

    private static IOpenserveRuntimeConfigProvider ConfigProvider(OpenserveFulfilmentSettings settings)
    {
        var m = new Mock<IOpenserveRuntimeConfigProvider>();
        m.Setup(x => x.Current).Returns(settings);
        return m.Object;
    }

    private static OpenserveOrderSubmissionService BuildService(
        SqliteTestDbFixture fixture, Mock<IOpenserveApiClient> client, OpenserveFulfilmentSettings? settings = null) =>
        new(fixture.AppDbContext, client.Object, new DefaultOpenserveSubscriberReferenceGenerator(),
            ConfigProvider(settings ?? EnabledSettings()), Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<OpenserveOrderSubmissionService>.Instance);

    private static Mock<IOpenserveApiClient> SuccessfulClient(string openserveOrderId = "302114")
    {
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Success(
                Guid.NewGuid().ToString(), "POST", "https://testapitrx.openserve.co.za/ws-ispcode/productOrder", 200,
                "{}", "{}", new OpenserveCreateOrderOutcome(openserveOrderId, "Validated", "Order Received.")));
        return client;
    }

    private async Task<(Order order, NetworkAccount account)> SeedFibreOrderAsync(
        SqliteTestDbFixture fixture, bool withAmid = true, bool withMapping = true)
    {
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"submit-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"Fibre 75Mbps {Guid.NewGuid():N}");
        var order = TestEntityFactory.CreateOrder(db, user, package, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        order.AddressLine1 = "61 Oak Ave";
        order.City = "Centurion";
        order.FullName = "Jane Doe";
        order.PhoneNumber = "0821234567";
        order.Email = user.Email;
        order.OpenserveAmId = withAmid ? "1000497" : null;
        await db.SaveChangesAsync();
        // An AMID alone isn't submittable — record the qualification evidence
        // (Fibre available, mapped product offered, address matched) with it.
        if (withAmid) await OpenserveEvidenceFixtures.SeedEligibleEvidenceAsync(db, order);

        if (withMapping)
        {
            db.PackageOpenserveMappings.Add(new PackageOpenserveMapping
            {
                Id = Guid.NewGuid(), ServicePackageId = package.Id, OpenserveProductName = "Openserve Fibre Connect",
                Sku = "OFC", Capacity = "75", CapacityUom = "Mbps", IsEnabled = true, CreatedAtUtc = DateTime.UtcNow
            });
        }

        var account = TestEntityFactory.CreateNetworkAccount(db, order, status: NetworkAccountStatus.Pending);
        await db.SaveChangesAsync();

        return (order, account);
    }

    [Fact]
    public async Task TrySubmitForOrderAsync_EligibleOrder_Submits()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (order, account) = await SeedFibreOrderAsync(fixture);
        var client = SuccessfulClient();
        var service = BuildService(fixture, client);

        var result = await service.TrySubmitForOrderAsync(order.Id, account.Id);

        Assert.True(result.IsSuccess);
        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Once);

        var stored = await fixture.DbContext.OpenserveOrders.SingleAsync(o => o.OrderId == order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Submitted, stored.NormalizedStatus);
        Assert.Equal("302114", stored.OpenserveOrderId);
        Assert.StartsWith("SF-", stored.ExternalReferenceNumber);
    }

    [Fact]
    public async Task TrySubmitForOrderAsync_SendsQualificationMduValues_AndExactIspIdentifier_AndPersistsSanitizedHeaders()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (order, account) = await SeedFibreOrderAsync(fixture);
        order.OpenserveAmId = "50782408";
        order.OpenserveBuildingNumId = "786154";
        order.OpenserveBuildingName = "EAGLES LANDING SHOPPING CENTRE";
        order.OpenserveFloor = "GROUND";
        order.OpenserveUnit = "12";
        order.UnitNumber = "Unit 12";                      // customer free text — must NOT be what's sent
        order.BuildingComplexName = "Eagles Landing Centre"; // customer free text — must NOT be what's sent
        await fixture.DbContext.SaveChangesAsync();
        await OpenserveEvidenceFixtures.SeedEligibleEvidenceAsync(fixture.AppDbContext, order); // evidence for the new AMID

        OpenserveCreateOrderCommand? captured = null;
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .Callback<OpenserveCreateOrderCommand, CancellationToken>((c, _) => captured = c)
            .ReturnsAsync(() => OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Success(Guid.NewGuid().ToString(), "POST", "https://stapitrx.openserve.co.za/ws-marut/productorder", 200,
                "{}", "{}", new OpenserveCreateOrderOutcome("1742148", "Validated", "Order received for processing. Order Id = 1742148. State = Validated"),
                requestHeadersJson: """{"MessageID":"m","FromLocation":"WS MARUT","SenderID":"SMARTFUTURE","ReplyToAddress":"https://stapitrx.openserve.co.za/ws-marut/productordercallback","api_key":"***REDACTED***"}"""));

        var settings = EnabledSettings();
        settings.IspIdentifier = "WS MARUT";
        var service = BuildService(fixture, client, settings);

        await service.TrySubmitForOrderAsync(order.Id, account.Id);

        Assert.NotNull(captured);
        Assert.Equal("WS MARUT", captured!.IspIdentifier);
        Assert.Equal("50782408", captured.Amid);
        Assert.Equal("786154", captured.BuildingNumId);
        Assert.Equal("EAGLES LANDING SHOPPING CENTRE", captured.BuildingName);
        Assert.Equal("GROUND", captured.Floor);
        Assert.Equal("12", captured.Unit);

        var log = await fixture.DbContext.OpenserveIntegrationLogs.SingleAsync(l => l.OperationType == OpenserveOperationType.CreateOrder);
        Assert.Contains("WS MARUT", log.RequestHeadersJson);
        Assert.Contains("REDACTED", log.RequestHeadersJson);
        var stored = await fixture.DbContext.OpenserveOrders.SingleAsync(o => o.OrderId == order.Id);
        Assert.Equal("1742148", stored.OpenserveOrderId); // correlation key for polling/callbacks
    }

    [Fact]
    public async Task TrySubmitForOrderAsync_UnmappedPackage_DoesNotSubmit()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (order, account) = await SeedFibreOrderAsync(fixture, withMapping: false);
        var client = SuccessfulClient();
        var service = BuildService(fixture, client);

        await service.TrySubmitForOrderAsync(order.Id, account.Id);

        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);

        var stored = await fixture.DbContext.OpenserveOrders.SingleAsync(o => o.OrderId == order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Failed, stored.NormalizedStatus);
        Assert.Contains("mapping", stored.LastFailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrySubmitForOrderAsync_MissingAmid_DoesNotSubmit()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (order, account) = await SeedFibreOrderAsync(fixture, withAmid: false);
        var client = SuccessfulClient();
        var service = BuildService(fixture, client);

        await service.TrySubmitForOrderAsync(order.Id, account.Id);

        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        var stored = await fixture.DbContext.OpenserveOrders.SingleAsync(o => o.OrderId == order.Id);
        Assert.Contains("AMID", stored.LastFailureMessage);
    }

    [Fact]
    public async Task TrySubmitForOrderAsync_CalledTwice_DoesNotDoubleSubmit()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (order, account) = await SeedFibreOrderAsync(fixture);
        var client = SuccessfulClient();
        var service = BuildService(fixture, client);

        await service.TrySubmitForOrderAsync(order.Id, account.Id);
        await service.TrySubmitForOrderAsync(order.Id, account.Id); // e.g. re-triggered from a second EnsurePendingForOrderAsync call

        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        var count = await fixture.DbContext.OpenserveOrders.CountAsync(o => o.OrderId == order.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TrySubmitForOrderAsync_ReservesSubscriberReferenceOnce_AndReusesItOnRetry()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (order, account) = await SeedFibreOrderAsync(fixture, withMapping: false); // blocked -> Failed, no HTTP call

        var client = SuccessfulClient();
        var service = BuildService(fixture, client);

        await service.TrySubmitForOrderAsync(order.Id, account.Id);
        var reloaded1 = await fixture.DbContext.NetworkAccounts.AsNoTracking().SingleAsync(n => n.Id == account.Id);
        var firstReference = reloaded1.OpenserveSubscriberReferenceNumber;
        Assert.False(string.IsNullOrWhiteSpace(firstReference));

        // Now map the package and retry — the reference must NOT change.
        fixture.DbContext.PackageOpenserveMappings.Add(new PackageOpenserveMapping
        {
            Id = Guid.NewGuid(), ServicePackageId = order.ServicePackageId!.Value, OpenserveProductName = "Openserve Fibre Connect",
            Sku = "OFC", Capacity = "75", CapacityUom = "Mbps", IsEnabled = true, CreatedAtUtc = DateTime.UtcNow
        });
        await fixture.DbContext.SaveChangesAsync();

        var openserveOrder = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == order.Id);
        var retryResult = await service.AdminRetrySubmissionAsync(openserveOrder.Id);

        Assert.True(retryResult.IsSuccess);
        var reloaded2 = await fixture.DbContext.NetworkAccounts.AsNoTracking().SingleAsync(n => n.Id == account.Id);
        Assert.Equal(firstReference, reloaded2.OpenserveSubscriberReferenceNumber);
        Assert.Equal(firstReference, retryResult.Data!.SubscriberReferenceNumber);
    }

    [Fact]
    public async Task AdminRetrySubmissionAsync_ReusesExactSameExternalReferenceNumber()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (order, account) = await SeedFibreOrderAsync(fixture);

        // 503 = Openserve definitely did not process it, so a plain retry is
        // allowed. (A TIMEOUT is outcome-unknown and needs Admin confirmation —
        // covered in OpenserveSubmissionRecoveryTests.)
        var failingClient = new Mock<IOpenserveApiClient>();
        failingClient.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Failure(
                Guid.NewGuid().ToString(), "POST", "endpoint", 503, "{}", "Service Unavailable", "ServiceUnavailable", "Openserve returned HTTP 503."));

        var service = BuildService(fixture, failingClient);
        await service.TrySubmitForOrderAsync(order.Id, account.Id);

        var afterFirst = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Failed, afterFirst.NormalizedStatus);
        Assert.Equal(1, afterFirst.RetryCount);
        var originalReference = afterFirst.ExternalReferenceNumber;

        // Retry succeeds this time.
        var succeedingClient = SuccessfulClient("999999");
        var retryService = BuildService(fixture, succeedingClient);
        var retryResult = await retryService.AdminRetrySubmissionAsync(afterFirst.Id);

        Assert.True(retryResult.IsSuccess);
        Assert.Equal(originalReference, retryResult.Data!.ExternalReferenceNumber);
        Assert.Equal(2, retryResult.Data.RetryCount);
        Assert.Equal(OpenserveProvisioningStatus.Submitted.ToString(), retryResult.Data.NormalizedStatus);
    }

    [Fact]
    public async Task TrySubmitForOrderAsync_NonFibrePackage_NeverSubmits()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, "security-order@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Security, name: "CCTV 4 IP");
        var order = TestEntityFactory.CreateOrder(db, user, package, orderNumber: "ORD-SEC-1");
        await db.SaveChangesAsync();
        var account = TestEntityFactory.CreateNetworkAccount(db, order, packageType: ServicePackageType.Security);
        await db.SaveChangesAsync();

        var client = SuccessfulClient();
        var service = BuildService(fixture, client);

        await service.TrySubmitForOrderAsync(order.Id, account.Id);

        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == order.Id));
    }

    [Fact]
    public async Task TrySubmitForOrderAsync_WhenDisabled_DoesNothing()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (order, account) = await SeedFibreOrderAsync(fixture);
        var client = SuccessfulClient();
        var service = BuildService(fixture, client, new OpenserveFulfilmentSettings { Enabled = false });

        var result = await service.TrySubmitForOrderAsync(order.Id, account.Id);

        Assert.True(result.IsSuccess);
        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == order.Id));
    }
}

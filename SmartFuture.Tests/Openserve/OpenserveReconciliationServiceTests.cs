using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Openserve;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Brief Priority 5/TEST REQUIREMENTS: only non-terminal orders are
// reconciled, and GET-discovered changes feed the SAME update pipeline
// as webhooks (no separate business logic).
public class OpenserveReconciliationServiceTests
{
    private static IOpenserveRuntimeConfigProvider Monitor(bool enabled = true)
    {
        var m = new Mock<IOpenserveRuntimeConfigProvider>();
        m.Setup(x => x.Current).Returns(new OpenserveFulfilmentSettings { Enabled = enabled, BaseUrl = "https://testapitrx.openserve.co.za", WsIspCode = "ws-ispcode" });
        return m.Object;
    }

    private async Task<OpenserveOrder> SeedAsync(SqliteTestDbFixture fixture, bool isTerminal, string? openserveOrderId = "302114")
    {
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"recon-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"Fibre {Guid.NewGuid():N}");
        var order = TestEntityFactory.CreateOrder(db, user, package, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        await db.SaveChangesAsync();
        TestEntityFactory.CreateNetworkAccount(db, order, status: NetworkAccountStatus.Pending);

        var openserveOrder = new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = order.Id, ExternalReferenceNumber = $"SF-{order.OrderNumber}",
            OpenserveOrderId = openserveOrderId, OrderType = "Sales Order", RawState = isTerminal ? "Accepted" : "In Progress",
            NormalizedStatus = isTerminal ? OpenserveProvisioningStatus.Completed : OpenserveProvisioningStatus.InProgress,
            IsTerminal = isTerminal, LastSuccessfulSyncAtUtc = DateTime.UtcNow.AddDays(-1), CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.OpenserveOrders.Add(openserveOrder);
        await db.SaveChangesAsync();
        return openserveOrder;
    }

    [Fact]
    public async Task ReconcileNonTerminalOrdersAsync_OnlyProcessesNonTerminalOrders()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var nonTerminal = await SeedAsync(fixture, isTerminal: false, openserveOrderId: "111111");
        var terminal = await SeedAsync(fixture, isTerminal: true, openserveOrderId: "222222");

        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.GetOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => OpenserveApiCallResult<OpenserveGetOrderOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveGetOrderOutcome(id, "In Progress", null, null, null)));

        var pipeline = new Mock<IOpenserveOrderUpdatePipeline>();
        pipeline.Setup(p => p.ApplyUpdateAsync(It.IsAny<OpenserveUpdateInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenserveUpdateOutcome { Kind = OpenserveUpdateResultKind.NoChange });

        var service = new OpenserveReconciliationService(
            fixture.AppDbContext, client.Object, pipeline.Object, Monitor(), Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<OpenserveReconciliationService>.Instance);

        var processed = await service.ReconcileNonTerminalOrdersAsync();

        Assert.Equal(1, processed);
        client.Verify(c => c.GetOrderAsync(nonTerminal.OpenserveOrderId!, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetOrderAsync(terminal.OpenserveOrderId!, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileNonTerminalOrdersAsync_WhenDisabled_ProcessesNothing()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        await SeedAsync(fixture, isTerminal: false);

        var client = new Mock<IOpenserveApiClient>();
        var pipeline = new Mock<IOpenserveOrderUpdatePipeline>();
        var service = new OpenserveReconciliationService(
            fixture.AppDbContext, client.Object, pipeline.Object, Monitor(enabled: false), Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<OpenserveReconciliationService>.Instance);

        var processed = await service.ReconcileNonTerminalOrdersAsync();

        Assert.Equal(0, processed);
        client.Verify(c => c.GetOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // End-to-end with the REAL OpenserveApiClient (fake HTTP handler) and the
    // REAL update pipeline: polling must hit the Postman "Query Order Details"
    // path, persist a sanitized log, apply the state once, and treat a
    // repeated identical poll as NoChange (no duplicate history).
    [Fact]
    public async Task ReconcileNonTerminalOrdersAsync_PollsPostmanGetProductOrderPath_AndStaysIdempotent()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedAsync(fixture, isTerminal: false, openserveOrderId: "1742148");

        var requestedUris = new List<string>();
        var handler = new StubHandler(req =>
        {
            requestedUris.Add(req.RequestUri!.ToString());
            return """{"@type":"Sales Order","id":"1742148","state":"Accepted","orderCharacteristic":[{"name":"OrderName","value":"SO1742148"}]}""";
        });
        var settings = new OpenserveFulfilmentSettings
        {
            Enabled = true, BaseUrl = "https://stapitrx.openserve.co.za", ApiKey = "fake-key-never-persisted", WsIspCode = "ws-marut", IspIdentifier = "WS MARUT",
            SenderId = "SMARTFUTURE", ReplyToAddress = "https://stapitrx.openserve.co.za/ws-marut/productordercallback"
        };
        var configProvider = new Mock<IOpenserveRuntimeConfigProvider>();
        configProvider.Setup(m => m.Current).Returns(settings);
        var client = new SmartFuture.Infrastructure.Openserve.OpenserveApiClient(new HttpClient(handler), configProvider.Object, NullLogger<SmartFuture.Infrastructure.Openserve.OpenserveApiClient>.Instance);

        var notifications = new Mock<IOpenserveCustomerNotificationService>();
        var pipeline = new OpenserveOrderUpdatePipeline(fixture.AppDbContext, Mock.Of<SmartFuture.Application.Orders.IOrderService>(), notifications.Object, Mock.Of<IAuditService>(),
            NullLogger<OpenserveOrderUpdatePipeline>.Instance);
        var service = new OpenserveReconciliationService(fixture.AppDbContext, client, pipeline, configProvider.Object, Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<OpenserveReconciliationService>.Instance);

        Assert.Equal(1, await service.ReconcileNonTerminalOrdersAsync());

        Assert.Equal("https://stapitrx.openserve.co.za/ws-marut/getproductorder/1742148", Assert.Single(requestedUris));
        var reloaded = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == openserveOrder.Id);
        Assert.Equal("Accepted", reloaded.RawState);
        Assert.True(reloaded.IsTerminal);
        Assert.Equal(1, await fixture.DbContext.OpenserveOrderStatusHistories.CountAsync(h => h.OpenserveOrderId == openserveOrder.Id));

        var log = await fixture.DbContext.OpenserveIntegrationLogs.SingleAsync(l => l.OperationType == OpenserveOperationType.GetOrder);
        Assert.Contains("/ws-marut/getproductorder/1742148", log.Endpoint);
        Assert.DoesNotContain("fake-key-never-persisted", log.RequestHeadersJson);
        Assert.Contains("WS MARUT", log.RequestHeadersJson);

        // Terminal now — the next tick must not poll it again at all.
        Assert.Equal(0, await service.ReconcileNonTerminalOrdersAsync());
        Assert.Single(requestedUris);

        // A manual re-sync returning the same state is NoChange: no duplicate history.
        await service.SynchronizeNowAsync(openserveOrder.Id, isManualTrigger: true);
        Assert.Equal(1, await fixture.DbContext.OpenserveOrderStatusHistories.CountAsync(h => h.OpenserveOrderId == openserveOrder.Id));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _body;
        public StubHandler(Func<HttpRequestMessage, string> body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(_body(request), System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task SynchronizeNowAsync_FeedsGetResultIntoSameUpdatePipelineAsWebhooks()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedAsync(fixture, isTerminal: false);

        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.GetOrderAsync(openserveOrder.OpenserveOrderId!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveGetOrderOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveGetOrderOutcome(openserveOrder.OpenserveOrderId, "Accepted", "SO303654", null, null)));

        var pipeline = new Mock<IOpenserveOrderUpdatePipeline>();
        pipeline.Setup(p => p.ApplyUpdateAsync(
                It.Is<OpenserveUpdateInput>(i => i.IsReconciliation && i.RawState == "Accepted" && i.OpenserveOrderId == openserveOrder.OpenserveOrderId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenserveUpdateOutcome { Kind = OpenserveUpdateResultKind.Applied });

        var service = new OpenserveReconciliationService(
            fixture.AppDbContext, client.Object, pipeline.Object, Monitor(), Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<OpenserveReconciliationService>.Instance);

        var result = await service.SynchronizeNowAsync(openserveOrder.Id);

        Assert.True(result.IsSuccess);
        pipeline.Verify(p => p.ApplyUpdateAsync(
            It.Is<OpenserveUpdateInput>(i => i.IsReconciliation), It.IsAny<CancellationToken>()), Times.Once);
    }
}

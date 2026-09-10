using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
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
    private static IOptionsMonitor<OpenserveFulfilmentSettings> Monitor(bool enabled = true)
    {
        var m = new Mock<IOptionsMonitor<OpenserveFulfilmentSettings>>();
        m.Setup(x => x.CurrentValue).Returns(new OpenserveFulfilmentSettings { Enabled = enabled, BaseUrl = "https://testapitrx.openserve.co.za", WsIspCode = "ws-ispcode" });
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
            fixture.AppDbContext, client.Object, pipeline.Object, Monitor(), NullLogger<OpenserveReconciliationService>.Instance);

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
            fixture.AppDbContext, client.Object, pipeline.Object, Monitor(enabled: false), NullLogger<OpenserveReconciliationService>.Instance);

        var processed = await service.ReconcileNonTerminalOrdersAsync();

        Assert.Equal(0, processed);
        client.Verify(c => c.GetOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
            fixture.AppDbContext, client.Object, pipeline.Object, Monitor(), NullLogger<OpenserveReconciliationService>.Instance);

        var result = await service.SynchronizeNowAsync(openserveOrder.Id);

        Assert.True(result.IsSuccess);
        pipeline.Verify(p => p.ApplyUpdateAsync(
            It.Is<OpenserveUpdateInput>(i => i.IsReconciliation), It.IsAny<CancellationToken>()), Times.Once);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Brief Priority 3/TEST REQUIREMENTS: the single shared update handler
// — event processing, duplicate idempotency, out-of-order handling,
// cancellation events, unknown-state retention, status history
// creation/non-duplication.
public class OpenserveOrderUpdatePipelineTests
{
    private static OpenserveOrderUpdatePipeline BuildPipeline(
        SqliteTestDbFixture fixture, Mock<IOrderService>? orderService = null, Mock<IOpenserveCustomerNotificationService>? notifications = null)
    {
        orderService ??= new Mock<IOrderService>();
        notifications ??= new Mock<IOpenserveCustomerNotificationService>();
        notifications.Setup(n => n.NotifyStatusChangedAsync(
                It.IsAny<OpenserveOrder>(), It.IsAny<Domain.Orders.Order>(),
                It.IsAny<OpenserveProvisioningStatus>(), It.IsAny<OpenserveProvisioningStatus>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new OpenserveOrderUpdatePipeline(
            fixture.AppDbContext, orderService.Object, notifications.Object, Mock.Of<IAuditService>(),
            NullLogger<OpenserveOrderUpdatePipeline>.Instance);
    }

    private async Task<OpenserveOrder> SeedSubmittedOrderAsync(SqliteTestDbFixture fixture, string openserveOrderId = "302114")
    {
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"pipeline-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"Fibre {Guid.NewGuid():N}");
        var order = TestEntityFactory.CreateOrder(db, user, package, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        await db.SaveChangesAsync();
        TestEntityFactory.CreateNetworkAccount(db, order, status: NetworkAccountStatus.Pending);

        var openserveOrder = new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = order.Id, ExternalReferenceNumber = $"SF-{order.OrderNumber}",
            OpenserveOrderId = openserveOrderId, OrderType = "Sales Order", RawState = "Validated",
            NormalizedStatus = OpenserveProvisioningStatus.Submitted, SubmittedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            LastOpenserveUpdateAtUtc = DateTime.UtcNow.AddMinutes(-10), CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        };
        db.OpenserveOrders.Add(openserveOrder);
        await db.SaveChangesAsync();
        return openserveOrder;
    }

    [Fact]
    public async Task ApplyUpdateAsync_ProductOrderStateChangeEvent_UpdatesOrderAndCreatesHistory()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedSubmittedOrderAsync(fixture);
        var pipeline = BuildPipeline(fixture);

        var outcome = await pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrder.OpenserveOrderId, RawState = "In Progress",
            EventType = OpenserveEventType.ProductOrderStateChangeEvent, OpenserveEventId = "evt-1",
            Description = "Status Changed to In Progress"
        });

        Assert.Equal(OpenserveUpdateResultKind.Applied, outcome.Kind);
        var reloaded = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == openserveOrder.Id);
        Assert.Equal("In Progress", reloaded.RawState);
        Assert.Equal(OpenserveProvisioningStatus.InProgress, reloaded.NormalizedStatus);

        var history = await fixture.DbContext.OpenserveOrderStatusHistories.Where(h => h.OpenserveOrderId == openserveOrder.Id).ToListAsync();
        Assert.Single(history);
        Assert.Equal("Validated", history[0].PreviousRawState);
        Assert.Equal("In Progress", history[0].NewRawState);
        Assert.Equal("Applied", history[0].ProcessingResult);
    }

    [Fact]
    public async Task ApplyUpdateAsync_DuplicateEventId_DoesNotCreateDuplicateHistoryOrNotify()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedSubmittedOrderAsync(fixture);
        var notifications = new Mock<IOpenserveCustomerNotificationService>();
        notifications.Setup(n => n.NotifyStatusChangedAsync(
                It.IsAny<OpenserveOrder>(), It.IsAny<Domain.Orders.Order>(),
                It.IsAny<OpenserveProvisioningStatus>(), It.IsAny<OpenserveProvisioningStatus>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var pipeline = BuildPipeline(fixture, notifications: notifications);

        var input = new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrder.OpenserveOrderId, RawState = "In Progress",
            EventType = OpenserveEventType.ProductOrderStateChangeEvent, OpenserveEventId = "evt-dup"
        };

        var first = await pipeline.ApplyUpdateAsync(input);
        var second = await pipeline.ApplyUpdateAsync(input);

        Assert.Equal(OpenserveUpdateResultKind.Applied, first.Kind);
        Assert.Equal(OpenserveUpdateResultKind.Duplicate, second.Kind);

        var historyCount = await fixture.DbContext.OpenserveOrderStatusHistories.CountAsync(h => h.OpenserveOrderId == openserveOrder.Id);
        Assert.Equal(1, historyCount);
        notifications.Verify(n => n.NotifyStatusChangedAsync(
            It.IsAny<OpenserveOrder>(), It.IsAny<Domain.Orders.Order>(),
            It.IsAny<OpenserveProvisioningStatus>(), It.IsAny<OpenserveProvisioningStatus>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyUpdateAsync_OutOfOrderEvent_RecordsHistoryButDoesNotRegressCurrentState()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedSubmittedOrderAsync(fixture);
        var pipeline = BuildPipeline(fixture);

        // Apply a forward event first (now current state = In Progress).
        await pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrder.OpenserveOrderId, RawState = "In Progress",
            EventOccurredAtUtc = DateTime.UtcNow, OpenserveEventId = "evt-forward"
        });

        // A late-arriving event claiming to have occurred BEFORE the
        // order was even submitted.
        var outOfOrder = await pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrder.OpenserveOrderId, RawState = "Acknowledged",
            EventOccurredAtUtc = DateTime.UtcNow.AddMinutes(-30), OpenserveEventId = "evt-late"
        });

        Assert.Equal(OpenserveUpdateResultKind.AppliedOutOfOrder, outOfOrder.Kind);

        var reloaded = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == openserveOrder.Id);
        Assert.Equal("In Progress", reloaded.RawState); // NOT regressed to Acknowledged

        var history = await fixture.DbContext.OpenserveOrderStatusHistories
            .Where(h => h.OpenserveOrderId == openserveOrder.Id).OrderBy(h => h.ReceivedAtUtc).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal("IgnoredOutOfOrder", history[1].ProcessingResult);
        Assert.Equal("Acknowledged", history[1].NewRawState); // still recorded for audit completeness
    }

    [Fact]
    public async Task ApplyUpdateAsync_CancelProductOrderStateChangeEvent_MarksTerminal()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedSubmittedOrderAsync(fixture);
        var pipeline = BuildPipeline(fixture);

        var outcome = await pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrder.OpenserveOrderId, RawState = "Cancelled",
            EventType = OpenserveEventType.CancelProductOrderStateChangeEvent, OpenserveEventId = "evt-cancel"
        });

        Assert.Equal(OpenserveUpdateResultKind.Applied, outcome.Kind);
        var reloaded = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == openserveOrder.Id);
        Assert.Equal(OpenserveProvisioningStatus.Cancelled, reloaded.NormalizedStatus);
        Assert.True(reloaded.IsTerminal);
    }

    [Fact]
    public async Task ApplyUpdateAsync_UnrecognisedRawState_IsRetainedAsUnknown_NotRejected()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedSubmittedOrderAsync(fixture);
        var pipeline = BuildPipeline(fixture);

        var outcome = await pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrder.OpenserveOrderId, RawState = "SomeFutureStateOpenserveInvents",
            OpenserveEventId = "evt-unknown"
        });

        Assert.Equal(OpenserveUpdateResultKind.Applied, outcome.Kind);
        var reloaded = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == openserveOrder.Id);
        Assert.Equal("SomeFutureStateOpenserveInvents", reloaded.RawState); // verbatim, not discarded
        Assert.Equal(OpenserveProvisioningStatus.Unknown, reloaded.NormalizedStatus);
        Assert.False(reloaded.IsTerminal);
    }

    [Fact]
    public async Task ApplyUpdateAsync_UnknownCorrelation_ReturnsUnknownOrder_DoesNotThrow()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var pipeline = BuildPipeline(fixture);

        var outcome = await pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = "999999999-does-not-exist", RawState = "Acknowledged"
        });

        Assert.Equal(OpenserveUpdateResultKind.UnknownOrder, outcome.Kind);
    }

    [Fact]
    public async Task ApplyUpdateAsync_Reconciliation_NoChange_DoesNotCreateHistory()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedSubmittedOrderAsync(fixture); // RawState = "Validated"
        var pipeline = BuildPipeline(fixture);

        var outcome = await pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrder.OpenserveOrderId, RawState = "Validated", IsReconciliation = true
        });

        Assert.Equal(OpenserveUpdateResultKind.NoChange, outcome.Kind);
        Assert.Equal(0, await fixture.DbContext.OpenserveOrderStatusHistories.CountAsync(h => h.OpenserveOrderId == openserveOrder.Id));
        var reloaded = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == openserveOrder.Id);
        Assert.NotNull(reloaded.LastSuccessfulSyncAtUtc);
    }

    [Fact]
    public async Task ApplyUpdateAsync_ReachingAccepted_TriggersAutoActivationAttempt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedSubmittedOrderAsync(fixture);

        var orderService = new Mock<IOrderService>();
        orderService.Setup(s => s.TryOpenserveConfirmedActivateServiceAsync(openserveOrder.OrderId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<OrderDto>.Success(new OrderDto { Status = OrderStatus.PendingActivation }, "Order is not yet eligible for activation."));

        var pipeline = BuildPipeline(fixture, orderService: orderService);

        await pipeline.ApplyUpdateAsync(new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrder.OpenserveOrderId, RawState = "Accepted",
            EventType = OpenserveEventType.ProductOrderStateChangeEvent, OpenserveEventId = "evt-accepted"
        });

        orderService.Verify(s => s.TryOpenserveConfirmedActivateServiceAsync(openserveOrder.OrderId, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyUpdateAsync_DuplicateAcceptedEvent_DoesNotDoubleTriggerActivation()
    {
        // Brief Priority 9: "The Openserve event must not accidentally
        // trigger duplicate invoices." The activation/pro-rata
        // idempotency itself lives in Order.FirstProRataInvoiceGeneratedAtUtc
        // (see FibreActivationProRataFactoryTests) — this test locks in
        // the OTHER half: a duplicate-delivered "Accepted" event must not
        // even ATTEMPT a second activation call.
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var openserveOrder = await SeedSubmittedOrderAsync(fixture);

        var orderService = new Mock<IOrderService>();
        orderService.Setup(s => s.TryOpenserveConfirmedActivateServiceAsync(openserveOrder.OrderId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<OrderDto>.Success(new OrderDto { Status = OrderStatus.Active }, IOrderService.OpenserveActivationSuccessMessage));

        var pipeline = BuildPipeline(fixture, orderService: orderService);

        var input = new OpenserveUpdateInput
        {
            OpenserveOrderId = openserveOrder.OpenserveOrderId, RawState = "Accepted",
            EventType = OpenserveEventType.ProductOrderStateChangeEvent, OpenserveEventId = "evt-accepted-dup"
        };

        await pipeline.ApplyUpdateAsync(input);
        await pipeline.ApplyUpdateAsync(input); // duplicate delivery of the exact same event

        orderService.Verify(s => s.TryOpenserveConfirmedActivateServiceAsync(openserveOrder.OrderId, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

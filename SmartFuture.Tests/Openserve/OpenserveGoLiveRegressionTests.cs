using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;
using Xunit;
using DomainOrder = SmartFuture.Domain.Orders.Order;

namespace SmartFuture.Tests.Openserve;

// Go-live regression coverage for the gaps found in the final Openserve audit:
//   1. Payment → network-account reservation → automatic Create Order (mocked Openserve client — never a live endpoint).
//   2. Inbound callback / event-notification PARSING (raw JSON through OpenserveInboundProcessor into the real update pipeline).
//   3. Environment-aware readiness wording (LIVE reads "PRODUCTION READY", UAT keeps "UAT READY").
public class OpenserveGoLiveRegressionTests
{
    private const string StagingCallback = "https://stapitrx.openserve.co.za/ws-marut/productordercallback";

    private static OpenserveFulfilmentSettings Settings(bool enabled = true) => new()
    {
        Enabled = enabled, BaseUrl = "https://stapitrx.openserve.co.za", ApiKey = "test-key", WsIspCode = "ws-marut", IspIdentifier = "WS MARUT",
        SenderId = "SMARTFUTURE", ReplyToAddress = StagingCallback
    };

    private static IOpenserveRuntimeConfigProvider Config(OpenserveFulfilmentSettings settings)
    {
        var provider = new Mock<IOpenserveRuntimeConfigProvider>();
        provider.Setup(p => p.Current).Returns(settings);
        return provider.Object;
    }

    private static Mock<IOpenserveApiClient> AcceptingClient(string openserveOrderId = "302114")
    {
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Success(Guid.NewGuid().ToString(), "POST", "https://stapitrx.openserve.co.za/ws-marut/productorder", 200,
                "{}", "{}", new OpenserveCreateOrderOutcome(openserveOrderId, "Validated", "Order Received for Processing.")));
        return client;
    }

    private static Mock<IOpenserveApiClient> TimingOutClient()
    {
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Failure(Guid.NewGuid().ToString(), "POST", "https://stapitrx.openserve.co.za/ws-marut/productorder", null,
                "{}", null, OpenserveApiErrorCodes.Timeout, "Openserve request timed out."));
        return client;
    }

    private static OpenserveOrderSubmissionService Submission(SqliteTestDbFixture fixture, Mock<IOpenserveApiClient> client, OpenserveFulfilmentSettings? settings = null) =>
        new(fixture.AppDbContext, client.Object, new DefaultOpenserveSubscriberReferenceGenerator(), Config(settings ?? Settings()), Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<OpenserveOrderSubmissionService>.Instance);

    private static NetworkAccountService NetworkAccounts(SqliteTestDbFixture fixture, IOpenserveOrderSubmissionService submission)
    {
        var provisioner = new Mock<INetworkProvisioner>();
        provisioner.SetupGet(p => p.ProviderName).Returns("NoOp");
        return new NetworkAccountService(fixture.AppDbContext, Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(), provisioner.Object, submission,
            NullLogger<NetworkAccountService>.Instance);
    }

    /// <summary>A paid Fibre order that passes every preflight rule (AMID + eligible evidence + enabled mapping) — but with NO network account yet: payment hasn't been applied.</summary>
    private static async Task<DomainOrder> SeedPaidEligibleFibreOrderAsync(SqliteTestDbFixture fixture)
    {
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"golive-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"Fibre 75Mbps {Guid.NewGuid():N}");
        var order = TestEntityFactory.CreateOrder(db, user, package, status: OrderStatus.PaymentReceived, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        // Qualified through Openserve at checkout — the only route Openserve submission accepts.
        order.FulfilmentRoute = OrderFulfilmentRoute.Openserve;
        order.AddressLine1 = "61 Oak Ave";
        order.City = "Centurion";
        order.FullName = "Jane Doe";
        order.PhoneNumber = "0821234567";
        order.Email = user.Email;
        order.OpenserveAmId = "1000497";
        await db.SaveChangesAsync();
        await OpenserveEvidenceFixtures.SeedEligibleEvidenceAsync(db, order);

        db.PackageOpenserveMappings.Add(new PackageOpenserveMapping
        {
            Id = Guid.NewGuid(), ServicePackageId = package.Id, OpenserveProductName = "Openserve Fibre Connect", Sku = "OFC", Capacity = "75", CapacityUom = "Mbps",
            IsEnabled = true, CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return order;
    }

    // ─── 1. Payment → automatic submission ───────────────────────────────

    [Fact]
    public async Task PaymentReservation_FiresTheAutomaticSubmission_ExactlyOnce()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var order = await SeedPaidEligibleFibreOrderAsync(fixture);
        var submission = new Mock<IOpenserveOrderSubmissionService>();
        submission.Setup(s => s.TrySubmitForOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success("ok"));
        var accounts = NetworkAccounts(fixture, submission.Object);

        var first = await accounts.EnsurePendingForOrderAsync(order.Id, NetworkAccountSource.SystemAutomated);
        var second = await accounts.EnsurePendingForOrderAsync(order.Id, NetworkAccountSource.SystemAutomated); // e.g. a replayed payment webhook

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        var account = await fixture.DbContext.NetworkAccounts.AsNoTracking().SingleAsync(n => n.OrderId == order.Id);
        Assert.Equal(NetworkAccountStatus.Pending, account.Status);
        submission.Verify(s => s.TrySubmitForOrderAsync(order.Id, account.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PaidEligibleFibreOrder_EndToEnd_SendsExactlyOneCreateOrder_ToTheMockedClient()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var order = await SeedPaidEligibleFibreOrderAsync(fixture);
        var client = AcceptingClient("302114");
        var submission = Submission(fixture, client);
        var accounts = NetworkAccounts(fixture, submission);

        await accounts.EnsurePendingForOrderAsync(order.Id, NetworkAccountSource.SystemAutomated);
        await accounts.EnsurePendingForOrderAsync(order.Id, NetworkAccountSource.SystemAutomated);
        var account = await fixture.DbContext.NetworkAccounts.AsNoTracking().SingleAsync(n => n.OrderId == order.Id);
        await submission.TrySubmitForOrderAsync(order.Id, account.Id); // a stray re-trigger must not double-submit

        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        var stored = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Submitted, stored.NormalizedStatus);
        Assert.Equal("302114", stored.OpenserveOrderId);
        Assert.Equal("smartfuture 60", stored.ExternalReferenceNumber); // the first sequential Openserve reference
    }

    [Fact]
    public async Task IntegrationDisabled_PaymentReservation_NeverCallsOpenserve()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var order = await SeedPaidEligibleFibreOrderAsync(fixture);
        var client = AcceptingClient();
        var accounts = NetworkAccounts(fixture, Submission(fixture, client, Settings(enabled: false)));

        var reserved = await accounts.EnsurePendingForOrderAsync(order.Id, NetworkAccountSource.SystemAutomated);

        Assert.True(reserved.IsSuccess); // the payment path itself is unaffected
        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == order.Id));
    }

    [Fact]
    public async Task PaymentReservation_TimeoutOnCreateOrder_IsOutcomeUnknown_AndNeverScheduledForAutomaticResend()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var order = await SeedPaidEligibleFibreOrderAsync(fixture);
        var client = TimingOutClient();
        var accounts = NetworkAccounts(fixture, Submission(fixture, client));

        await accounts.EnsurePendingForOrderAsync(order.Id, NetworkAccountSource.SystemAutomated);

        var stored = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Failed, stored.NormalizedStatus);
        Assert.Equal(OpenserveSubmissionFailureClass.OutcomeUnknown, stored.LastFailureClass);
        Assert.Null(stored.NextAutomaticRetryAtUtc);
        Assert.Null(stored.OpenserveOrderId);
        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── 2. Inbound callback / event parsing ─────────────────────────────

    private static OpenserveInboundProcessor Inbound(SqliteTestDbFixture fixture, Mock<IOrderService>? orderService = null)
    {
        var notifications = new Mock<IOpenserveCustomerNotificationService>();
        notifications.Setup(n => n.NotifyStatusChangedAsync(It.IsAny<OpenserveOrder>(), It.IsAny<DomainOrder>(), It.IsAny<OpenserveProvisioningStatus>(), It.IsAny<OpenserveProvisioningStatus>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var pipeline = new OpenserveOrderUpdatePipeline(fixture.AppDbContext, (orderService ?? new Mock<IOrderService>()).Object, notifications.Object, Mock.Of<IAuditService>(),
            NullLogger<OpenserveOrderUpdatePipeline>.Instance);
        return new OpenserveInboundProcessor(fixture.AppDbContext, pipeline, NullLogger<OpenserveInboundProcessor>.Instance);
    }

    private static async Task<OpenserveOrder> SeedForwardedOrderAsync(SqliteTestDbFixture fixture, string openserveOrderId, OrderStatus orderStatus = OrderStatus.PaymentReceived)
    {
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"inbound-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"Fibre {Guid.NewGuid():N}");
        var order = TestEntityFactory.CreateOrder(db, user, package, status: orderStatus, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        await db.SaveChangesAsync();
        TestEntityFactory.CreateNetworkAccount(db, order, status: NetworkAccountStatus.Pending);

        var openserveOrder = new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = order.Id, ExternalReferenceNumber = $"SF-{order.OrderNumber}", OpenserveOrderId = openserveOrderId, OrderType = "Sales Order",
            RawState = "Validated", NormalizedStatus = OpenserveProvisioningStatus.Submitted, SubmittedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            LastOpenserveUpdateAtUtc = DateTime.UtcNow.AddMinutes(-10), CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        };
        db.OpenserveOrders.Add(openserveOrder);
        await db.SaveChangesAsync();
        return openserveOrder;
    }

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static string StateChangeEvent(long eventId, string openserveOrderId, string state) => $$"""
        {
          "eventId": {{eventId}},
          "eventTime": "{{Now()}}",
          "eventType": "ProductOrderStateChangeEvent",
          "correlationId": "corr-{{eventId}}",
          "description": "Status Changed to {{state}}",
          "timeOcurred": "{{Now()}}",
          "event": {
            "productOrder": {
              "id": "{{openserveOrderId}}",
              "state": "{{state}}",
              "orderCharacteristic": [ { "name": "OrderName", "value": "ON-{{openserveOrderId}}" } ]
            }
          }
        }
        """;

    private static async Task<OpenserveIntegrationLog> LatestInboundLogAsync(SqliteTestDbFixture fixture) =>
        await fixture.DbContext.OpenserveIntegrationLogs.AsNoTracking().Where(l => l.Direction == OpenserveIntegrationDirection.Inbound).OrderByDescending(l => l.OccurredAtUtc).FirstAsync();

    [Fact]
    public async Task Callback_DocumentedShape_UpdatesOnlyTheMatchingOrder_AndLinksTheLog()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var target = await SeedForwardedOrderAsync(fixture, "302114");
        var bystander = await SeedForwardedOrderAsync(fixture, "302115");

        const string payload = """{"Result":{"ResultCode":"0","ResultMsgCode":"","ResultMsg":"Order accepted for processing"},"Payload":{"order":{"id":"302114"},"state":"In Progress"}}""";
        var result = await Inbound(fixture).ProcessCallbackAsync(payload, new Dictionary<string, string>());

        Assert.True(result.Accepted);
        Assert.Equal(nameof(OpenserveUpdateResultKind.Applied), result.Message);
        var updated = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == target.Id);
        Assert.Equal("In Progress", updated.RawState);
        Assert.Equal(OpenserveProvisioningStatus.InProgress, updated.NormalizedStatus);
        var untouched = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == bystander.Id);
        Assert.Equal("Validated", untouched.RawState);

        var log = await LatestInboundLogAsync(fixture);
        Assert.Equal(OpenserveOperationType.CallbackInbound, log.OperationType);
        Assert.True(log.IsSuccess);
        Assert.Equal(target.Id, log.OpenserveOrderId);
    }

    [Fact]
    public async Task Callback_NumericResultCode_AndNoState_IsRecordedAsAcknowledged()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var target = await SeedForwardedOrderAsync(fixture, "302114");

        var result = await Inbound(fixture).ProcessCallbackAsync("""{"Result":{"ResultCode":0},"Payload":{"order":{"id":"302114"}}}""", new Dictionary<string, string>());

        Assert.True(result.Accepted);
        var updated = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == target.Id);
        Assert.Equal("Acknowledged", updated.RawState);
        Assert.Equal(OpenserveProvisioningStatus.Submitted, updated.NormalizedStatus);
    }

    [Fact]
    public async Task Callback_Accepted_OnAPaidOrder_RunsTheActivationHookOnce()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var target = await SeedForwardedOrderAsync(fixture, "302114", OrderStatus.PendingActivation);
        var orderService = new Mock<IOrderService>();
        orderService.Setup(s => s.TryOpenserveConfirmedActivateServiceAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<OrderDto>.Success(new OrderDto { Status = OrderStatus.PendingActivation }, "Order is not yet eligible for activation."));
        var inbound = Inbound(fixture, orderService);

        const string accepted = """{"Result":{"ResultCode":"0"},"Payload":{"order":{"id":"302114"},"state":"Accepted"}}""";
        await inbound.ProcessCallbackAsync(accepted, new Dictionary<string, string>());
        await inbound.ProcessCallbackAsync(accepted, new Dictionary<string, string>()); // replayed callback

        var updated = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == target.Id);
        Assert.Equal(OpenserveProvisioningStatus.Completed, updated.NormalizedStatus);
        Assert.True(updated.IsTerminal);
        orderService.Verify(s => s.TryOpenserveConfirmedActivateServiceAsync(target.OrderId, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Event_ProductOrderStateChange_IsAppliedOnce_AndADuplicateEventIdIsIgnored()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var target = await SeedForwardedOrderAsync(fixture, "302114");
        var inbound = Inbound(fixture);
        var payload = StateChangeEvent(9001, "302114", "In Progress");

        var first = await inbound.ProcessEventAsync(payload, new Dictionary<string, string>());
        var second = await inbound.ProcessEventAsync(payload, new Dictionary<string, string>());

        Assert.Equal(nameof(OpenserveUpdateResultKind.Applied), first.Message);
        Assert.Equal(nameof(OpenserveUpdateResultKind.Duplicate), second.Message);
        var updated = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == target.Id);
        Assert.Equal(OpenserveProvisioningStatus.InProgress, updated.NormalizedStatus);
        Assert.Equal("ON-302114", updated.OpenserveOrderName);
        Assert.Equal("corr-9001", updated.CorrelationId);
        var history = await fixture.DbContext.OpenserveOrderStatusHistories.AsNoTracking().Where(h => h.OpenserveOrderId == target.Id).ToListAsync();
        var entry = Assert.Single(history);
        Assert.Equal("9001", entry.OpenserveEventId);
        Assert.Equal(OpenserveEventType.ProductOrderStateChangeEvent, entry.EventType);
    }

    [Fact]
    public async Task Event_CancelProductOrderStateChange_MarksTheOrderTerminal()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var target = await SeedForwardedOrderAsync(fixture, "302114");
        var payload = $$"""
            {"eventId": 9002, "eventType": "CancelProductOrderStateChangeEvent", "timeOcurred": "{{Now()}}",
             "event": { "cancelproductorder": { "id": "302114", "state": "Cancelled" } } }
            """;

        var result = await Inbound(fixture).ProcessEventAsync(payload, new Dictionary<string, string>());

        Assert.True(result.Accepted);
        var updated = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == target.Id);
        Assert.Equal(OpenserveProvisioningStatus.Cancelled, updated.NormalizedStatus);
        Assert.True(updated.IsTerminal);
    }

    [Fact]
    public async Task UnknownOpenserveOrderId_IsLoggedAsUnmatched_AndChangesNothing()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var existing = await SeedForwardedOrderAsync(fixture, "302114");

        var result = await Inbound(fixture).ProcessCallbackAsync("""{"Result":{"ResultCode":"0"},"Payload":{"order":{"id":"999999"},"state":"Accepted"}}""", new Dictionary<string, string>());

        Assert.True(result.Accepted); // never give Openserve a reason to retry forever
        Assert.Equal(nameof(OpenserveUpdateResultKind.UnknownOrder), result.Message);
        var log = await LatestInboundLogAsync(fixture);
        Assert.False(log.IsSuccess);
        Assert.Contains("999999", log.ErrorSummary);
        var untouched = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == existing.Id);
        Assert.Equal("Validated", untouched.RawState);
    }

    [Fact]
    public async Task PayloadSentToTheWrongReceiver_IsLoggedButNeverApplied()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var target = await SeedForwardedOrderAsync(fixture, "302114");
        var inbound = Inbound(fixture);

        // An event notification posted to /callback, and a callback posted to /events.
        var eventOnCallback = await inbound.ProcessCallbackAsync(StateChangeEvent(9003, "302114", "In Progress"), new Dictionary<string, string>());
        var callbackOnEvents = await inbound.ProcessEventAsync("""{"Result":{"ResultCode":"0"},"Payload":{"order":{"id":"302114"},"state":"In Progress"}}""", new Dictionary<string, string>());

        Assert.True(eventOnCallback.Accepted);
        Assert.True(callbackOnEvents.Accepted);
        var unchanged = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.Id == target.Id);
        Assert.Equal("Validated", unchanged.RawState);
        Assert.Equal(2, await fixture.DbContext.OpenserveIntegrationLogs.CountAsync(l => l.Direction == OpenserveIntegrationDirection.Inbound && !l.IsSuccess));
    }

    [Fact]
    public async Task MalformedJson_IsAcceptedAndLoggedForReview()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();

        var result = await Inbound(fixture).ProcessCallbackAsync("{not json", new Dictionary<string, string>());

        Assert.True(result.Accepted);
        var log = await LatestInboundLogAsync(fixture);
        Assert.False(log.IsSuccess);
        Assert.Equal("Malformed JSON.", log.ErrorSummary);
    }

    // ─── 3. Environment-aware readiness wording ──────────────────────────

    [Theory]
    [InlineData("Production", true)]
    [InlineData("production", true)]
    [InlineData("Live", true)]
    [InlineData("UAT", false)]
    [InlineData("Staging", false)]
    [InlineData("Development", false)]
    [InlineData(null, false)]
    public void ProductionLikeEnvironment_IsOurOwnLiveDeployment(string? environmentName, bool expected) =>
        Assert.Equal(expected, OpenserveIntegrationAdminService.IsProductionLikeEnvironment(environmentName));

    [Fact]
    public void ReadinessHeading_SaysProductionOnLive_AndUatElsewhere()
    {
        Assert.Equal("PRODUCTION READY — 14/14 checks passed", OpenserveIntegrationAdminService.ReadinessSummary(true, 14, 14, productionLike: true));
        Assert.Equal("UAT READY — 14/14 checks passed", OpenserveIntegrationAdminService.ReadinessSummary(true, 14, 14, productionLike: false));
        Assert.Equal("NOT READY — 13/14 checks passed", OpenserveIntegrationAdminService.ReadinessSummary(false, 13, 14, productionLike: true));
        Assert.Equal("NOT READY — 13/14 checks passed", OpenserveIntegrationAdminService.ReadinessSummary(false, 13, 14, productionLike: false));
    }

    [Fact]
    public void OverviewBadge_ReadyForUat_ReadsReadyForProductionOnLive_StateKeyUnchanged()
    {
        Assert.Equal("Ready for production", OpenserveIntegrationAdminService.OverallStateLabelFor("ReadyForUat", "Ready for UAT", productionLike: true));
        Assert.Equal("Ready for UAT", OpenserveIntegrationAdminService.OverallStateLabelFor("ReadyForUat", "Ready for UAT", productionLike: false));
        Assert.Equal("Healthy", OpenserveIntegrationAdminService.OverallStateLabelFor("Healthy", "Healthy", productionLike: true));
        Assert.Equal("Disabled", OpenserveIntegrationAdminService.OverallStateLabelFor("Disabled", "Disabled", productionLike: true));
    }
}

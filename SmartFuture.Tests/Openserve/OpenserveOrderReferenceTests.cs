using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Infrastructure.Data;
using SmartFuture.Infrastructure.Openserve;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;
using DomainOrder = SmartFuture.Domain.Orders.Order;
using F = SmartFuture.Tests.Openserve.OpenserveEvidenceFixtures;
using H = SmartFuture.Tests.Openserve.OpenserveQualificationOrchestrationTests;

namespace SmartFuture.Tests.Openserve;

// Openserve order references: "smartfuture 60", "smartfuture 61", … — the
// convention SmartFuture staff used for manual Openserve portal orders, sent as
// the mandatory, unique productCharacteristic "External Reference Number"
// (spec ITSD-179559 §4.1.2.6 + Appendix C). One per order, allocated just before
// the first real send, persisted before the request, reused by every retry,
// never given to an order SmartFuture fulfils itself. Mocked Openserve only.
public class OpenserveOrderReferenceTests
{
    private static readonly OpenserveOrderReferenceSettings Defaults = new();

    // ─── Format (pure) ────────────────────────────────────────────────

    [Theory]
    [InlineData("smartfuture 60", 60)]
    [InlineData("SmartFuture 61", 61)]
    [InlineData("  smartfuture   62 ", 62)]
    [InlineData("smartfuture60", null)]
    [InlineData("smartfuture 0", null)]
    [InlineData("smartfuture 6a", null)]
    [InlineData("SF-SF-20261025-AB12CD", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_the_prefix_a_space_and_a_positive_number_is_a_sequential_reference(string? reference, int? expected)
    {
        Assert.Equal(expected, OpenserveOrderReference.Parse(reference, Defaults));
    }

    [Fact]
    public void The_default_format_is_exactly_what_staff_used_by_hand()
    {
        Assert.Equal("smartfuture 60", OpenserveOrderReference.Format(Defaults, 60));
        Assert.Equal(60, OpenserveOrderReference.FirstNumberOf(Defaults));
        Assert.Equal("smartfuture 7", OpenserveOrderReference.Format(new OpenserveOrderReferenceSettings { Prefix = "  " }, 7));
        Assert.Equal(1, OpenserveOrderReference.FirstNumberOf(new OpenserveOrderReferenceSettings { FirstNumber = -5 }));
    }

    [Theory]
    [InlineData(OpenserveSubmissionFailureClass.None, null, false, false)]
    [InlineData(OpenserveSubmissionFailureClass.Blocked, "BLOCKED_MAPPING", false, false)]
    [InlineData(OpenserveSubmissionFailureClass.Retryable, OpenserveOrderReference.InternalErrorCode, false, false)]
    [InlineData(OpenserveSubmissionFailureClass.Retryable, "ServiceUnavailable", false, true)]
    [InlineData(OpenserveSubmissionFailureClass.NonRetryable, "GEN-45994", false, true)]
    [InlineData(OpenserveSubmissionFailureClass.OutcomeUnknown, "TIMEOUT", false, true)]
    [InlineData(OpenserveSubmissionFailureClass.None, null, true, true)]
    public void A_reference_that_may_have_reached_Openserve_is_never_replaced(OpenserveSubmissionFailureClass failureClass, string? code, bool hasCreateOrderLog, bool expected)
    {
        var record = new OpenserveOrder { NormalizedStatus = OpenserveProvisioningStatus.Failed, LastFailureClass = failureClass, LastFailureCode = code };
        Assert.Equal(expected, OpenserveOrderReference.MayHaveReachedOpenserve(record, hasCreateOrderLog));
    }

    [Fact]
    public void An_order_always_derives_the_same_submission_record_key()
    {
        var orderId = Guid.NewGuid();
        Assert.Equal(OpenserveOrderReference.SalesOrderRecordId(orderId), OpenserveOrderReference.SalesOrderRecordId(orderId));
        Assert.NotEqual(OpenserveOrderReference.SalesOrderRecordId(orderId), OpenserveOrderReference.SalesOrderRecordId(Guid.NewGuid()));
    }

    // ─── Harness ──────────────────────────────────────────────────────

    private sealed class RecordingClient
    {
        private int _nextOpenserveId = 302114;
        public ConcurrentQueue<OpenserveCreateOrderCommand> Sent { get; } = new();
        public Queue<Func<OpenserveApiCallResult<OpenserveCreateOrderOutcome>>> Script { get; } = new();
        public Action<OpenserveCreateOrderCommand>? OnSend { get; set; }
        public Mock<IOpenserveApiClient> Mock { get; } = new();

        public RecordingClient()
        {
            Mock.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((OpenserveCreateOrderCommand command, CancellationToken _) =>
                {
                    OnSend?.Invoke(command);
                    Sent.Enqueue(command);
                    lock (Script)
                    {
                        if (Script.Count > 0) return Script.Dequeue()();
                    }
                    return Accepted(Interlocked.Increment(ref _nextOpenserveId).ToString(CultureInfo.InvariantCulture));
                });
        }

        public IReadOnlyList<string> References => Sent.Select(c => c.ExternalReferenceNumber).ToList();
    }

    private static OpenserveApiCallResult<OpenserveCreateOrderOutcome> Accepted(string id) =>
        OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Success(Guid.NewGuid().ToString(), "POST", "https://stapitrx.openserve.co.za/ws-marut/productorder", 200, "{}", "{}",
            new OpenserveCreateOrderOutcome(id, "Validated", $"Order received for processing. Order Id = {id}. State = Validated"));

    private static OpenserveApiCallResult<OpenserveCreateOrderOutcome> ServiceUnavailable() =>
        OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Failure(Guid.NewGuid().ToString(), "POST", "https://stapitrx.openserve.co.za/ws-marut/productorder", 503, "{}", "Service Unavailable",
            "ServiceUnavailable", "Openserve returned HTTP 503.");

    private static OpenserveApiCallResult<OpenserveCreateOrderOutcome> Timeout() =>
        OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Failure(Guid.NewGuid().ToString(), "POST", "https://stapitrx.openserve.co.za/ws-marut/productorder", null, "{}", null,
            OpenserveApiErrorCodes.Timeout, "Openserve request timed out.");

    private static OpenserveOrderSubmissionService Submission(IAppDbContext db, IOpenserveApiClient client, OpenserveFulfilmentSettings? settings = null) =>
        new(db, client, new DefaultOpenserveSubscriberReferenceGenerator(), H.Config(settings ?? H.Settings()), new AuditService(db, NullLogger<AuditService>.Instance), H.CurrentUser(null),
            NullLogger<OpenserveOrderSubmissionService>.Instance);

    private static async Task<ServicePackage> FibrePackageAsync(IAppDbContext db)
    {
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"Fibre 50 {Guid.NewGuid():N}");
        package.DownloadSpeedMbps = 50;
        await db.SaveChangesAsync();
        db.PackageOpenserveMappings.Add(new PackageOpenserveMapping
        {
            ServicePackageId = package.Id, Sku = "OFC", Capacity = "50", CapacityUom = "Mbps", OpenserveProductName = "Openserve Fibre Connect", IsEnabled = true
        });
        await db.SaveChangesAsync();
        return package;
    }

    /// <summary>A paid Openserve-routed Fibre order that passes every preflight rule, with a Pending network account.</summary>
    private static async Task<(DomainOrder Order, NetworkAccount Account)> EligibleOrderAsync(IAppDbContext db, ServicePackage package, OrderFulfilmentRoute route = OrderFulfilmentRoute.Openserve,
        bool withAmid = true)
    {
        var user = TestEntityFactory.CreateUser(db, $"ref-{Guid.NewGuid():N}@example.com");
        var order = TestEntityFactory.CreateOrder(db, user, package, status: OrderStatus.PaymentReceived, orderNumber: $"SF-{Guid.NewGuid():N}"[..12]);
        order.FulfilmentRoute = route;
        order.AddressLine1 = "61 Oak Ave";
        order.FullName = "Jane Doe";
        order.PhoneNumber = "0821234567";
        order.Latitude = -26.095950m;
        order.Longitude = 27.927632m;
        order.OpenserveAmId = withAmid ? H.Amid : null;
        await db.SaveChangesAsync();
        if (withAmid) await F.SeedEligibleEvidenceAsync(db, order);
        var account = TestEntityFactory.CreateNetworkAccount(db, order, status: NetworkAccountStatus.Pending);
        await db.SaveChangesAsync();
        return (order, account);
    }

    private static Task<OpenserveSubmissionAttemptDto> SendAsync(OpenserveOrderSubmissionService service, Guid orderId, OpenserveSubmissionTrigger trigger = OpenserveSubmissionTrigger.AutomaticInitial,
        bool confirmOutcomeUnknown = false) =>
        service.SubmitAsync(new OpenserveSubmissionRequest(orderId, trigger) { ConfirmOutcomeUnknown = confirmOutcomeUnknown }).ContinueWith(t =>
        {
            Assert.True(t.Result.IsSuccess, t.Result.Message);
            return t.Result.Data!;
        });

    private static Task<OpenserveOrder> RecordAsync(SqliteTestDbFixture fixture, Guid orderId) =>
        fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);

    // ─── 1. Sequence starting at 60 ───────────────────────────────────

    [Fact]
    public async Task References_start_at_smartfuture_60_and_count_up_one_per_order()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        var service = Submission(fixture.AppDbContext, client.Mock.Object);

        var orders = new List<DomainOrder>();
        for (var i = 0; i < 3; i++) orders.Add((await EligibleOrderAsync(fixture.AppDbContext, package)).Order);
        foreach (var order in orders) await SendAsync(service, order.Id);

        Assert.Equal(new[] { "smartfuture 60", "smartfuture 61", "smartfuture 62" }, client.References);
        for (var i = 0; i < 3; i++)
        {
            var record = await RecordAsync(fixture, orders[i].Id);
            Assert.Equal(OpenserveOrderReference.SalesOrderRecordId(orders[i].Id), record.Id); // the per-order race guard
            Assert.Equal($"smartfuture {60 + i}", record.ExternalReferenceNumber);
            Assert.Equal(OpenserveProvisioningStatus.Submitted, record.NormalizedStatus);
            Assert.Equal((302115 + i).ToString(CultureInfo.InvariantCulture), record.OpenserveOrderId); // Openserve's own id, untouched
            Assert.StartsWith("SF-", orders[i].OrderNumber); // SmartFuture's order number, untouched
        }
    }

    [Fact]
    public async Task Sixty_is_only_used_if_it_is_free_and_numbering_continues_after_the_highest_used()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        var service = Submission(fixture.AppDbContext, client.Mock.Object);

        // An order that already holds "smartfuture 60".
        var (holder, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        fixture.AppDbContext.OpenserveOrders.Add(new OpenserveOrder
        {
            Id = OpenserveOrderReference.SalesOrderRecordId(holder.Id), OrderId = holder.Id, ExternalReferenceNumber = "smartfuture 60", OrderType = OpenserveSubmissionRules.SalesOrderType,
            NormalizedStatus = OpenserveProvisioningStatus.Submitted, SubmittedAtUtc = DateTime.UtcNow, OpenserveOrderId = "300000", CreatedAtUtc = DateTime.UtcNow
        });
        await fixture.AppDbContext.SaveChangesAsync();

        var (first, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        await SendAsync(service, first.Id);
        Assert.Equal("smartfuture 61", client.References.Last());

        // A gap (e.g. 75 set by hand on another record) — the sequence continues after the highest.
        var record = await fixture.DbContext.OpenserveOrders.SingleAsync(o => o.OrderId == holder.Id);
        record.ExternalReferenceNumber = "smartfuture 75";
        await fixture.DbContext.SaveChangesAsync();
        var (second, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        await SendAsync(service, second.Id);
        Assert.Equal("smartfuture 76", client.References.Last());
    }

    [Fact]
    public async Task A_configured_first_number_is_honoured_without_a_code_change()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var settings = H.Settings();
        settings.OrderReference.FirstNumber = 100;
        var client = new RecordingClient();
        var (order, _) = await EligibleOrderAsync(fixture.AppDbContext, package);

        await SendAsync(Submission(fixture.AppDbContext, client.Mock.Object, settings), order.Id);

        Assert.Equal("smartfuture 100", Assert.Single(client.References));
    }

    // ─── 3. Persistence across restarts ───────────────────────────────

    [Fact]
    public async Task The_sequence_survives_a_restart_because_it_is_read_from_the_database()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        var (first, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        var (second, _) = await EligibleOrderAsync(fixture.AppDbContext, package);

        await SendAsync(Submission(fixture.AppDbContext, client.Mock.Object), first.Id);

        // The API restarts: a new process, a new context, a new service.
        await using var restarted = fixture.CreateSiblingContext();
        await SendAsync(Submission(restarted, client.Mock.Object), second.Id);

        Assert.Equal(new[] { "smartfuture 60", "smartfuture 61" }, client.References);
    }

    // ─── 4. + 5. Retries and unknown outcomes keep the reference ──────

    [Fact]
    public async Task A_retry_resends_the_same_reference_and_consumes_no_new_number()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        client.Script.Enqueue(ServiceUnavailable);
        var service = Submission(fixture.AppDbContext, client.Mock.Object);
        var (order, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        var (next, _) = await EligibleOrderAsync(fixture.AppDbContext, package);

        var failed = await SendAsync(service, order.Id);
        Assert.Equal(OpenserveSubmissionOutcome.Failed, failed.Outcome);
        Assert.Equal(OpenserveSubmissionFailureClass.Retryable, (await RecordAsync(fixture, order.Id)).LastFailureClass);

        await SendAsync(service, order.Id, OpenserveSubmissionTrigger.AdminManual);
        await SendAsync(service, next.Id);

        Assert.Equal(new[] { "smartfuture 60", "smartfuture 60", "smartfuture 61" }, client.References);
        Assert.Equal(1, await fixture.DbContext.OpenserveOrders.CountAsync(o => o.OrderId == order.Id));
    }

    [Fact]
    public async Task An_unknown_outcome_keeps_its_reference_for_the_confirmed_retry()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        client.Script.Enqueue(Timeout);
        var service = Submission(fixture.AppDbContext, client.Mock.Object);
        var (order, _) = await EligibleOrderAsync(fixture.AppDbContext, package);

        await SendAsync(service, order.Id);
        var unknown = await RecordAsync(fixture, order.Id);
        Assert.Equal(OpenserveSubmissionFailureClass.OutcomeUnknown, unknown.LastFailureClass);
        Assert.Equal("smartfuture 60", unknown.ExternalReferenceNumber);

        // Never automatic; an Admin who confirmed with Openserve (searching "smartfuture 60") resends it.
        var automatic = await service.SubmitAsync(new OpenserveSubmissionRequest(order.Id, OpenserveSubmissionTrigger.BackgroundRetry));
        Assert.False(automatic.IsSuccess);
        await SendAsync(service, order.Id, OpenserveSubmissionTrigger.AdminManual, confirmOutcomeUnknown: true);

        Assert.Equal(new[] { "smartfuture 60", "smartfuture 60" }, client.References);
    }

    [Fact]
    public async Task The_reference_is_persisted_before_the_request_leaves()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        var (order, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        string? storedAtSendTime = null;
        await using var observer = fixture.CreateSiblingContext();
        client.OnSend = _ => storedAtSendTime = observer.OpenserveOrders.AsNoTracking().Single(o => o.OrderId == order.Id).ExternalReferenceNumber;

        await SendAsync(Submission(fixture.AppDbContext, client.Mock.Object), order.Id);

        Assert.Equal("smartfuture 60", storedAtSendTime); // a crash mid-call leaves exactly the reference Openserve may have received
    }

    [Fact]
    public async Task Historical_references_are_never_renumbered_and_dont_affect_the_sequence()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        var service = Submission(fixture.AppDbContext, client.Mock.Object);
        var (legacy, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        var historical = OpenserveOrderReference.Provisional(legacy.OrderNumber);
        var record = new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = legacy.Id, ExternalReferenceNumber = historical, OrderType = OpenserveSubmissionRules.SalesOrderType,
            NormalizedStatus = OpenserveProvisioningStatus.Failed, LastFailureClass = OpenserveSubmissionFailureClass.Retryable, LastFailureCode = "ServiceUnavailable",
            LastSubmissionAttemptAtUtc = DateTime.UtcNow.AddHours(-1), CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
        };
        fixture.AppDbContext.OpenserveOrders.Add(record);
        fixture.AppDbContext.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(), OpenserveOrderId = record.Id, Direction = OpenserveIntegrationDirection.Outbound, OperationType = OpenserveOperationType.CreateOrder,
            HttpMethod = "POST", ResponseStatusCode = 503, OccurredAtUtc = DateTime.UtcNow.AddHours(-1), IsSuccess = false
        });
        await fixture.AppDbContext.SaveChangesAsync();
        var (fresh, _) = await EligibleOrderAsync(fixture.AppDbContext, package);

        await SendAsync(service, legacy.Id, OpenserveSubmissionTrigger.AdminManual);
        await SendAsync(service, fresh.Id);

        Assert.Equal(new[] { historical, "smartfuture 60" }, client.References);
        Assert.Equal(historical, (await RecordAsync(fixture, legacy.Id)).ExternalReferenceNumber);
    }

    [Fact]
    public async Task A_blocked_attempt_sends_nothing_and_consumes_no_number()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        var service = Submission(fixture.AppDbContext, client.Mock.Object);
        var (blockedOrder, _) = await EligibleOrderAsync(fixture.AppDbContext, package, withAmid: false);
        var (eligible, _) = await EligibleOrderAsync(fixture.AppDbContext, package);

        var blocked = await SendAsync(service, blockedOrder.Id);
        await SendAsync(service, eligible.Id);

        Assert.Equal(OpenserveSubmissionOutcome.Blocked, blocked.Outcome);
        Assert.Equal(OpenserveOrderReference.Provisional(blockedOrder.OrderNumber), (await RecordAsync(fixture, blockedOrder.Id)).ExternalReferenceNumber);
        Assert.Equal(new[] { "smartfuture 60" }, client.References);
    }

    // ─── 6. The exact Product Order payload field ─────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            const string ack = """{"ErrorResult":{"code":"0","reason":"OS-ESB-ACK","message":"Order received for processing. Order Id = 1742148. State = Validated"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ack, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task The_real_api_client_sends_it_as_the_External_Reference_Number_productCharacteristic()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var (order, account) = await EligibleOrderAsync(fixture.AppDbContext, package);
        var handler = new CapturingHandler();
        var apiClient = new OpenserveApiClient(new HttpClient(handler), H.Config(H.Settings()), NullLogger<OpenserveApiClient>.Instance);

        await SendAsync(Submission(fixture.AppDbContext, apiClient), order.Id);

        using var body = JsonDocument.Parse(handler.Body!);
        var product = body.RootElement.GetProperty("productOrderItem")[0].GetProperty("product");
        var productCharacteristics = product.GetProperty("productCharacteristic").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("value").GetString());
        Assert.Equal("smartfuture 60", productCharacteristics["External Reference Number"]);

        // Unchanged: the per-subscriber reference and the SmartFuture order number in the comment.
        var serviceCharacteristics = product.GetProperty("realizingService")[0].GetProperty("serviceCharacteristic").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("value").GetString());
        var stored = await fixture.DbContext.NetworkAccounts.AsNoTracking().SingleAsync(n => n.Id == account.Id);
        Assert.Equal(stored.OpenserveSubscriberReferenceNumber, serviceCharacteristics["Subscriber Reference Number"]);
        Assert.Contains(order.OrderNumber, handler.Body);

        var record = await RecordAsync(fixture, order.Id);
        Assert.Equal("1742148", record.OpenserveOrderId);
        Assert.Contains("smartfuture 60", (await fixture.DbContext.OpenserveIntegrationLogs.AsNoTracking().SingleAsync(l => l.OpenserveOrderId == record.Id)).RequestBodyJson);
    }

    // ─── 7. Callbacks and reconciliation still match ──────────────────

    [Fact]
    public async Task Callbacks_events_and_reconciliation_still_match_by_Openserve_order_id()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        var (order, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        await SendAsync(Submission(fixture.AppDbContext, client.Mock.Object), order.Id);
        var record = await RecordAsync(fixture, order.Id);

        var notifications = new Mock<IOpenserveCustomerNotificationService>();
        notifications.Setup(n => n.NotifyStatusChangedAsync(It.IsAny<OpenserveOrder>(), It.IsAny<DomainOrder>(), It.IsAny<OpenserveProvisioningStatus>(), It.IsAny<OpenserveProvisioningStatus>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var pipeline = new OpenserveOrderUpdatePipeline(fixture.AppDbContext, new Mock<IOrderService>().Object, notifications.Object, Mock.Of<IAuditService>(),
            NullLogger<OpenserveOrderUpdatePipeline>.Instance);

        // Inbound event (Openserve's own id is the only key it carries).
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var evt = $$"""{ "eventId": 4711, "eventTime": "{{now}}", "eventType": "ProductOrderStateChangeEvent", "correlationId": "corr-1", "timeOcurred": "{{now}}", "event": { "productOrder": { "id": "{{record.OpenserveOrderId}}", "state": "In Progress" } } }""";
        var inbound = await new OpenserveInboundProcessor(fixture.AppDbContext, pipeline, NullLogger<OpenserveInboundProcessor>.Instance).ProcessEventAsync(evt, new Dictionary<string, string>());
        Assert.True(inbound.Accepted);
        Assert.Equal(OpenserveProvisioningStatus.InProgress, (await RecordAsync(fixture, order.Id)).NormalizedStatus);

        // Reconciliation GETs by the same id.
        client.Mock.Setup(c => c.GetOrderAsync(record.OpenserveOrderId!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveGetOrderOutcome>.Success(Guid.NewGuid().ToString(), "GET", "https://stapitrx.openserve.co.za/ws-marut/getproductorder", 200, "", "{}",
                new OpenserveGetOrderOutcome(record.OpenserveOrderId, "Accepted", "SO303654", null, null)));
        var reconciliation = new OpenserveReconciliationService(fixture.AppDbContext, client.Mock.Object, pipeline, H.Config(H.Settings()), Mock.Of<IAuditService>(), H.CurrentUser(null),
            NullLogger<OpenserveReconciliationService>.Instance);
        var synced = await reconciliation.SynchronizeNowAsync(record.Id);

        Assert.True(synced.IsSuccess, synced.Message);
        var final = await RecordAsync(fixture, order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Completed, final.NormalizedStatus);
        Assert.Equal("smartfuture 60", final.ExternalReferenceNumber); // never touched by inbound updates
    }

    // ─── 8. Internal inclusion-list orders never get a reference ──────

    [Fact]
    public async Task An_internal_inclusion_order_never_receives_an_Openserve_reference_on_any_path()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var client = new RecordingClient();
        var service = Submission(fixture.AppDbContext, client.Mock.Object);
        var (internalOrder, _) = await EligibleOrderAsync(fixture.AppDbContext, package, OrderFulfilmentRoute.InternalInclusion);
        var (openserveOrder, _) = await EligibleOrderAsync(fixture.AppDbContext, package);

        foreach (var trigger in new[] { OpenserveSubmissionTrigger.AutomaticInitial, OpenserveSubmissionTrigger.SafetySweep, OpenserveSubmissionTrigger.BackgroundRetry, OpenserveSubmissionTrigger.AdminManual })
        {
            var refused = await service.SubmitAsync(new OpenserveSubmissionRequest(internalOrder.Id, trigger));
            Assert.False(refused.IsSuccess);
            Assert.Equal(OpenserveSubmissionRules.InternalInclusionReason, refused.Message);
        }
        await SendAsync(service, openserveOrder.Id);

        Assert.False(await fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == internalOrder.Id));
        Assert.Equal(new[] { "smartfuture 60" }, client.References); // nothing was consumed by the internal order
    }

    // ─── 2. + 9. Concurrency: no duplicate references or submissions ─

    private sealed class TakeTheNumberFirst : SaveChangesInterceptor
    {
        private readonly Func<Task> _takeIt;
        private bool _fired;

        public TakeTheNumberFirst(Func<Task> takeIt) => _takeIt = takeIt;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var allocating = eventData.Context!.ChangeTracker.Entries<OpenserveOrder>()
                .Any(e => e.State == EntityState.Modified && e.Entity.ExternalReferenceNumber == "smartfuture 60");
            if (allocating && !_fired)
            {
                _fired = true;
                await _takeIt();
            }
            return result;
        }
    }

    [Fact]
    public async Task When_another_order_takes_the_number_between_read_and_write_the_next_number_is_used()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await FibrePackageAsync(fixture.AppDbContext);
        var (rival, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        var (order, _) = await EligibleOrderAsync(fixture.AppDbContext, package);
        var client = new RecordingClient();

        // Exactly when this instance is about to save "smartfuture 60", another
        // instance commits "smartfuture 60" for a different order.
        await using var instance = fixture.CreateSiblingContext(new TakeTheNumberFirst(async () =>
        {
            await using var other = fixture.CreateSiblingContext();
            other.OpenserveOrders.Add(new OpenserveOrder
            {
                Id = OpenserveOrderReference.SalesOrderRecordId(rival.Id), OrderId = rival.Id, ExternalReferenceNumber = "smartfuture 60", OrderType = OpenserveSubmissionRules.SalesOrderType,
                NormalizedStatus = OpenserveProvisioningStatus.Submitted, SubmittedAtUtc = DateTime.UtcNow, OpenserveOrderId = "300001", CreatedAtUtc = DateTime.UtcNow
            });
            await other.SaveChangesAsync();
        }));

        await SendAsync(Submission(instance, client.Mock.Object), order.Id);

        Assert.Equal(new[] { "smartfuture 61" }, client.References);
        var references = await fixture.DbContext.OpenserveOrders.AsNoTracking().Select(o => o.ExternalReferenceNumber).ToListAsync();
        Assert.Equal(new[] { "smartfuture 60", "smartfuture 61" }, references.OrderBy(r => r));
    }

    [Fact]
    public async Task Concurrent_submissions_on_separate_connections_get_distinct_contiguous_references()
    {
        // A real file database so each "API instance" has its own connection.
        var path = Path.Combine(Path.GetTempPath(), $"sf-openserve-ref-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False;Default Timeout=60";
        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        try
        {
            List<Guid> orderIds;
            await using (var setup = new SqliteTestDbFixture.SqliteAppDbContext(Options()))
            {
                await setup.Database.EnsureCreatedAsync();
                var package = await FibrePackageAsync(setup);
                orderIds = new List<Guid>();
                for (var i = 0; i < 6; i++) orderIds.Add((await EligibleOrderAsync(setup, package)).Order.Id);
            }

            var client = new RecordingClient();
            var results = await Task.WhenAll(orderIds.Select(id => Task.Run(async () =>
            {
                await using var db = new SqliteTestDbFixture.SqliteAppDbContext(Options());
                return await Submission(db, client.Mock.Object).SubmitAsync(new OpenserveSubmissionRequest(id, OpenserveSubmissionTrigger.AutomaticInitial));
            })));

            Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
            Assert.Equal(Enumerable.Range(60, 6).Select(n => $"smartfuture {n}"), client.References.OrderBy(r => int.Parse(r[12..], CultureInfo.InvariantCulture)));
            await using var check = new SqliteTestDbFixture.SqliteAppDbContext(Options());
            Assert.Equal(6, await check.OpenserveOrders.Select(o => o.ExternalReferenceNumber).Distinct().CountAsync());
            Assert.Equal(6, await check.OpenserveOrders.Select(o => o.OrderId).Distinct().CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

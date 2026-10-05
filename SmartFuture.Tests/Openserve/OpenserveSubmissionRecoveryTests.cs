using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Installations;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Auditing;
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
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Openserve order reliability / recovery: one submission coordinator for
// every path (automatic, Admin Send/Retry, background retry, safety sweep),
// failure classification, per-order pause, and the Admin Order Detail view.
// Numbers in the region headers refer to the required test list.
public class OpenserveSubmissionRecoveryTests
{
    private const string FakeApiKey = "fake-recovery-api-key-7d31c9e2";
    private const string Endpoint = "https://stapitrx.openserve.co.za/ws-marut/productorder";

    // ─── harness ────────────────────────────────────────────────────

    private static OpenserveFulfilmentSettings Settings(bool enabled = true, Action<OpenserveSubmissionRecoverySettings>? recovery = null)
    {
        var settings = new OpenserveFulfilmentSettings
        {
            Enabled = enabled, BaseUrl = "https://stapitrx.openserve.co.za", ApiKey = FakeApiKey, WsIspCode = "ws-marut", IspIdentifier = "WS MARUT",
            SenderId = "SMARTFUTURE", ReplyToAddress = "https://stapitrx.openserve.co.za/ws-marut/productordercallback"
        };
        recovery?.Invoke(settings.SubmissionRecovery);
        return settings;
    }

    private static IOpenserveRuntimeConfigProvider Config(OpenserveFulfilmentSettings settings)
    {
        var m = new Mock<IOpenserveRuntimeConfigProvider>();
        m.Setup(x => x.Current).Returns(settings);
        return m.Object;
    }

    private static ICurrentUserService CurrentUser(Guid? userId)
    {
        var m = new Mock<ICurrentUserService>();
        m.SetupGet(c => c.UserId).Returns(userId);
        return m.Object;
    }

    private static OpenserveOrderSubmissionService Submission(IAppDbContext db, Mock<IOpenserveApiClient> client, OpenserveFulfilmentSettings? settings = null, Guid? userId = null,
        ILogger<OpenserveOrderSubmissionService>? logger = null) =>
        new(db, client.Object, new DefaultOpenserveSubscriberReferenceGenerator(), Config(settings ?? Settings()), new AuditService(db, NullLogger<AuditService>.Instance), CurrentUser(userId),
            logger ?? NullLogger<OpenserveOrderSubmissionService>.Instance);

    private static OpenserveSubmissionRecoveryService Recovery(IAppDbContext db, IOpenserveOrderSubmissionService submission, OpenserveFulfilmentSettings? settings = null,
        ILogger<OpenserveSubmissionRecoveryService>? logger = null) =>
        new(db, submission, Config(settings ?? Settings()), logger ?? NullLogger<OpenserveSubmissionRecoveryService>.Instance);

    private static OpenserveOrderFulfilmentService Fulfilment(IAppDbContext db, IOpenserveOrderSubmissionService submission, OpenserveFulfilmentSettings? settings = null, Guid? userId = null,
        IOpenserveQualificationService? qualification = null) =>
        new(db, submission, Config(settings ?? Settings()), new AuditService(db, NullLogger<AuditService>.Instance), CurrentUser(userId),
            qualification ?? new OpenserveQualificationService(db, Mock.Of<IOpenserveApiClient>(), Config(settings ?? Settings()), NullLogger<OpenserveQualificationService>.Instance),
            NullLogger<OpenserveOrderFulfilmentService>.Instance);

    private static OpenserveApiCallResult<OpenserveCreateOrderOutcome> Accepted(string openserveOrderId = "1742148") =>
        OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Success(Guid.NewGuid().ToString(), "POST", Endpoint, 200, "{}", "{}",
            new OpenserveCreateOrderOutcome(openserveOrderId, "Validated", $"Order received for processing. Order Id = {openserveOrderId}. State = Validated"),
            "{\"api_key\":\"***\"}");

    private static OpenserveApiCallResult<OpenserveCreateOrderOutcome> Failed(int? status, string code, string message) =>
        OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Failure(Guid.NewGuid().ToString(), "POST", Endpoint, status, "{}", status is null ? null : "{}", code, message, "{\"api_key\":\"***\"}");

    private static OpenserveApiCallResult<OpenserveCreateOrderOutcome> ServiceUnavailable() => Failed(503, "ServiceUnavailable", "Openserve returned HTTP 503.");
    private static OpenserveApiCallResult<OpenserveCreateOrderOutcome> ConnectionFailed() => Failed(null, OpenserveApiErrorCodes.ConnectionFailed, "No such host is known.");
    private static OpenserveApiCallResult<OpenserveCreateOrderOutcome> Timeout() => Failed(null, OpenserveApiErrorCodes.Timeout, "Openserve request timed out.");

    /// <summary>Each CreateOrder call takes the next scripted result (the last one repeats) and records the command.</summary>
    private static Mock<IOpenserveApiClient> Client(List<OpenserveCreateOrderCommand> sent, params OpenserveApiCallResult<OpenserveCreateOrderOutcome>[] results)
    {
        var queue = new Queue<OpenserveApiCallResult<OpenserveCreateOrderOutcome>>(results);
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OpenserveCreateOrderCommand command, CancellationToken _) =>
            {
                sent.Add(command);
                return queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            });
        return client;
    }

    private sealed record Seeded(Order Order, NetworkAccount? Account, ServicePackage Package);

    private static async Task<Seeded> SeedAsync(SqliteTestDbFixture fixture, bool withMapping = true, bool withAmid = true, OrderStatus status = OrderStatus.PaymentReceived,
        NetworkAccountStatus accountStatus = NetworkAccountStatus.Pending, DateTime? accountCreatedAtUtc = null, ServicePackageType type = ServicePackageType.Fibre, bool withAccount = true)
    {
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"recovery-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: type, name: $"SmartFuture Fibre 100 {Guid.NewGuid():N}");
        var order = TestEntityFactory.CreateOrder(db, user, package, status: status, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        order.AddressLine1 = "61 Oak Ave";
        order.Suburb = "Highveld";
        order.City = "Centurion";
        order.FullName = "Jane Doe";
        order.PhoneNumber = "0821234567";
        order.Email = user.Email;
        order.OpenserveAmId = withAmid ? "1000497" : null;
        await db.SaveChangesAsync();
        if (withAmid) await OpenserveEvidenceFixtures.SeedEligibleEvidenceAsync(db, order);

        if (withMapping)
        {
            db.PackageOpenserveMappings.Add(new PackageOpenserveMapping
            {
                Id = Guid.NewGuid(), ServicePackageId = package.Id, OpenserveProductName = "Openserve Fibre Connect", Sku = "OFC", Capacity = "100", CapacityUom = "Mbps",
                IsEnabled = true, CreatedAtUtc = DateTime.UtcNow
            });
        }

        NetworkAccount? account = null;
        if (withAccount)
        {
            account = TestEntityFactory.CreateNetworkAccount(db, order, status: accountStatus, packageType: type);
            account.CreatedAtUtc = accountCreatedAtUtc ?? DateTime.UtcNow.AddHours(-1);
        }
        await db.SaveChangesAsync();
        return new Seeded(order, account, package);
    }

    private static Task<Guid> AdminAsync(SqliteTestDbFixture fixture) => AdminAsync(fixture, "Thandi", "Admin");

    private static async Task<Guid> AdminAsync(SqliteTestDbFixture fixture, string firstName, string lastName)
    {
        var admin = TestEntityFactory.CreateUser(fixture.AppDbContext, $"admin-{Guid.NewGuid():N}@example.com", firstName, lastName);
        await fixture.AppDbContext.SaveChangesAsync();
        return admin.Id;
    }

    private static Task<OpenserveOrder> RecordAsync(SqliteTestDbFixture fixture, Guid orderId) =>
        fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);

    private static Task MakeRetryDueAsync(SqliteTestDbFixture fixture, Guid orderId) =>
        fixture.DbContext.OpenserveOrders.Where(o => o.OrderId == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAutomaticRetryAtUtc, DateTime.UtcNow.AddMinutes(-1)));

    private static OpenserveOrder SubmittedRecord(Order order, OpenserveProvisioningStatus status = OpenserveProvisioningStatus.Submitted) => new()
    {
        Id = Guid.NewGuid(), OrderId = order.Id, ExternalReferenceNumber = $"SF-{order.OrderNumber}", OrderType = OpenserveSubmissionRules.SalesOrderType,
        OpenserveOrderId = "1742148", NormalizedStatus = status, RawState = "Validated", SubmittedAtUtc = DateTime.UtcNow.AddHours(-2), RetryCount = 1,
        IsTerminal = status is OpenserveProvisioningStatus.Completed or OpenserveProvisioningStatus.Cancelled, CreatedAtUtc = DateTime.UtcNow.AddHours(-2)
    };

    // ─── 1 / 2. Manual send through the shared pipeline ─────────────

    [Fact]
    public async Task EligibleFibreOrder_CanBeManuallySent_FromOrderDetail()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()), userId: adminId);
        var fulfilment = Fulfilment(fixture.AppDbContext, submission, userId: adminId);

        var before = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        Assert.False(before.ForwardedToOpenserve);
        Assert.Equal(OpenserveFulfilmentState.NotSubmitted, before.State);
        Assert.True(before.ManualSubmission.Allowed, before.ManualSubmission.Reason);
        Assert.Equal("Send", before.ManualSubmission.Action);

        var result = await fulfilment.SubmitAsync(seeded.Order.Id, confirmOutcomeUnknown: false);

        Assert.True(result.IsSuccess, result.Message);
        var after = result.Data!;
        Assert.True(after.ForwardedToOpenserve);
        Assert.Equal(OpenserveFulfilmentState.Submitted, after.State);
        Assert.Equal("1742148", after.OpenserveOrderId);
        Assert.False(after.ManualSubmission.Allowed);
        var command = Assert.Single(sent);
        Assert.Equal($"SF-{seeded.Order.OrderNumber}", command.ExternalReferenceNumber);
    }

    [Fact]
    public async Task ManualSubmission_UsesTheSamePipeline_AsAutomaticSubmission()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var automaticOrder = await SeedAsync(fixture);
        var manualOrder = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, Accepted("1"), Accepted("2"));

        await Submission(fixture.AppDbContext, client).TrySubmitForOrderAsync(automaticOrder.Order.Id, automaticOrder.Account!.Id);
        await Fulfilment(fixture.AppDbContext, Submission(fixture.AppDbContext, client, userId: adminId), userId: adminId).SubmitAsync(manualOrder.Order.Id, false);

        Assert.Equal(2, sent.Count);
        // Same builder: identical payload apart from the per-order references.
        static object Shape(OpenserveCreateOrderCommand c) => new { c.OpenserveProductName, c.Sku, c.Capacity, c.CapacityUom, c.IspIdentifier, c.Amid, c.Street1, c.City, c.SubscriberContactName };
        Assert.Equal(JsonSerializer.Serialize(Shape(sent[0])), JsonSerializer.Serialize(Shape(sent[1])));

        foreach (var (order, trigger) in new[] { (automaticOrder.Order, OpenserveSubmissionTrigger.AutomaticInitial), (manualOrder.Order, OpenserveSubmissionTrigger.AdminManual) })
        {
            var record = await RecordAsync(fixture, order.Id);
            Assert.Equal(OpenserveProvisioningStatus.Submitted, record.NormalizedStatus);
            Assert.Equal(trigger, record.LastSubmissionTrigger);
            Assert.False(string.IsNullOrWhiteSpace(record.SubscriberReferenceNumber));
            var log = await fixture.DbContext.OpenserveIntegrationLogs.AsNoTracking().SingleAsync(l => l.OpenserveOrderId == record.Id && l.OperationType == OpenserveOperationType.CreateOrder);
            Assert.Equal("POST", log.HttpMethod);
            Assert.Equal(trigger, log.SubmissionTrigger);
            Assert.True(await fixture.DbContext.OpenserveOrderStatusHistories.AnyAsync(h => h.OpenserveOrderId == record.Id));
        }
    }

    // ─── 3 / 19 / 20. Retry ──────────────────────────────────────────

    [Fact]
    public async Task RetryableFailure_CanBeManuallyRetried_ReusingTheSameOrderAndReferences()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, ServiceUnavailable(), Accepted());

        await Submission(fixture.AppDbContext, client).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        var failed = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Failed, failed.NormalizedStatus);
        Assert.Equal(OpenserveSubmissionFailureClass.Retryable, failed.LastFailureClass);
        Assert.NotNull(failed.NextAutomaticRetryAtUtc);

        var fulfilment = Fulfilment(fixture.AppDbContext, Submission(fixture.AppDbContext, client, userId: adminId), userId: adminId);
        var view = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        Assert.Equal(OpenserveFulfilmentState.FailedRetryable, view.State);
        Assert.Equal("FAILED — RETRYABLE", view.StateLabel);
        Assert.True(view.AutomaticRetry.Allowed, view.AutomaticRetry.Reason);
        Assert.True(view.ManualSubmission.Allowed);
        Assert.Equal("Retry", view.ManualSubmission.Action);
        Assert.False(view.ManualSubmission.RequiresOutcomeConfirmation);

        var retried = await fulfilment.SubmitAsync(seeded.Order.Id, false);

        Assert.True(retried.IsSuccess, retried.Message);
        Assert.True(retried.Data!.ForwardedToOpenserve);
        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0].ExternalReferenceNumber, sent[1].ExternalReferenceNumber);
        Assert.Equal(sent[0].SubscriberReferenceNumber, sent[1].SubscriberReferenceNumber);
        Assert.Equal(1, await fixture.DbContext.Orders.CountAsync(o => o.Id == seeded.Order.Id));
        var record = Assert.Single(await fixture.DbContext.OpenserveOrders.AsNoTracking().Where(o => o.OrderId == seeded.Order.Id).ToListAsync());
        Assert.Equal(failed.Id, record.Id);
        Assert.Equal(2, record.RetryCount);
        Assert.Null(record.NextAutomaticRetryAtUtc);
    }

    [Fact]
    public async Task Retry_UsesTheCurrentEnabledPackageMapping()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, ServiceUnavailable(), Accepted());
        await Submission(fixture.AppDbContext, client).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);

        // Admin corrects the package mapping before the retry.
        await fixture.DbContext.PackageOpenserveMappings.Where(m => m.ServicePackageId == seeded.Package.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Capacity, "75"));
        await MakeRetryDueAsync(fixture, seeded.Order.Id);
        await Recovery(fixture.AppDbContext, Submission(fixture.AppDbContext, client)).RunRetryPassAsync();

        Assert.Equal(new[] { "100", "75" }, sent.Select(c => c.Capacity));
    }

    // ─── 4 / 5 / 6 / 7. Never resubmit / gates ──────────────────────

    [Fact]
    public async Task SuccessfullySubmittedOrder_CannotBePostedAgain_ByAnyPath()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, Accepted());
        var submission = Submission(fixture.AppDbContext, client, userId: adminId);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        var record = await RecordAsync(fixture, seeded.Order.Id);

        var manual = await Fulfilment(fixture.AppDbContext, submission, userId: adminId).SubmitAsync(seeded.Order.Id, confirmOutcomeUnknown: true);
        var consoleRetry = await submission.AdminRetrySubmissionAsync(record.Id, confirmOutcomeUnknown: true);
        var background = await submission.SubmitAsync(new OpenserveSubmissionRequest(seeded.Order.Id, OpenserveSubmissionTrigger.BackgroundRetry));
        var again = await submission.SubmitAsync(new OpenserveSubmissionRequest(seeded.Order.Id, OpenserveSubmissionTrigger.AutomaticInitial));
        var recovery = Recovery(fixture.AppDbContext, submission);
        await recovery.RunRetryPassAsync();
        await recovery.RunSafetySweepAsync();

        Assert.Single(sent);
        Assert.Equal(ErrorCodes.CONFLICT, manual.Code);
        Assert.Contains("Already submitted to Openserve", manual.Message);
        Assert.Equal(ErrorCodes.CONFLICT, consoleRetry.Code);
        Assert.False(background.IsSuccess);
        Assert.False(again.IsSuccess);
    }

    [Fact]
    public async Task CompletedOpenserveOrder_CannotBeResubmitted_EvenWithoutAnOrderId()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var completed = SubmittedRecord(seeded.Order, OpenserveProvisioningStatus.Completed);
        completed.OpenserveOrderId = null;
        completed.SubmittedAtUtc = null; // status alone must be enough
        fixture.AppDbContext.OpenserveOrders.Add(completed);
        await fixture.AppDbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()));

        var view = (await Fulfilment(fixture.AppDbContext, submission).GetAsync(seeded.Order.Id)).Data!;
        var attempt = await submission.SubmitAsync(new OpenserveSubmissionRequest(seeded.Order.Id, OpenserveSubmissionTrigger.AdminManual) { ConfirmOutcomeUnknown = true });

        Assert.Equal(OpenserveFulfilmentState.Completed, view.State);
        Assert.False(view.ManualSubmission.Allowed);
        Assert.Equal(ErrorCodes.CONFLICT, attempt.Code);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task CancelledSmartFutureOrder_CannotBeSubmitted()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, status: OrderStatus.Cancelled);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()));
        var fulfilment = Fulfilment(fixture.AppDbContext, submission);

        var view = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        var manual = await fulfilment.SubmitAsync(seeded.Order.Id, false);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);

        Assert.Equal(OpenserveFulfilmentState.OrderCancelled, view.State);
        Assert.False(view.ManualSubmission.Allowed);
        Assert.False(manual.IsSuccess);
        Assert.Contains("Cancelled", manual.Message);
        Assert.Empty(sent);
        Assert.False(await fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == seeded.Order.Id));
    }

    [Fact]
    public async Task IntegrationDisabled_BlocksManualSubmission_NoKillSwitchBypass()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var disabled = Settings(enabled: false);
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()), disabled);
        var fulfilment = Fulfilment(fixture.AppDbContext, submission, disabled);

        var view = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        var manual = await fulfilment.SubmitAsync(seeded.Order.Id, confirmOutcomeUnknown: true);
        var sweep = await Recovery(fixture.AppDbContext, submission, disabled).RunSafetySweepAsync();

        Assert.Equal(OpenserveFulfilmentState.IntegrationDisabled, view.State);
        Assert.False(view.ManualSubmission.Allowed);
        Assert.Equal("Openserve integration is disabled.", view.ManualSubmission.Reason);
        Assert.False(manual.IsSuccess);
        Assert.Equal("Openserve integration is disabled.", manual.Message);
        Assert.Equal(0, sweep.Candidates);
        Assert.Empty(sent);
        Assert.False(await fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == seeded.Order.Id));
    }

    // ─── 8 / 9. Correctable blockers are explained, never guessed ────

    [Fact]
    public async Task MissingMapping_BlocksSubmission_WithAClearReason()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, withMapping: false);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()));
        var fulfilment = Fulfilment(fixture.AppDbContext, submission);

        var view = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        Assert.Equal(OpenserveFulfilmentState.BlockedPackageMapping, view.State);
        Assert.Equal("BLOCKED — PACKAGE MAPPING", view.StateLabel);
        Assert.False(view.ManualSubmission.Allowed);
        Assert.Contains("No enabled Openserve package mapping", view.ManualSubmission.Reason);

        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Empty(sent);
        Assert.Equal(OpenserveSubmissionFailureClass.Blocked, record.LastFailureClass);
        Assert.Equal(OpenserveBlockedCodes.Mapping, record.LastFailureCode);
        Assert.Null(record.NextAutomaticRetryAtUtc);
        var afterView = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        Assert.False(afterView.AutomaticRetry.Allowed);
        Assert.Contains("not retried automatically", afterView.AutomaticRetry.Reason);
    }

    [Fact]
    public async Task MissingAmid_BlocksSafely_EvenIfASendIsForced()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, withAmid: false);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()));
        var fulfilment = Fulfilment(fixture.AppDbContext, submission);

        var view = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        // A stale page could still POST — the backend re-checks and sends nothing.
        var forced = await fulfilment.SubmitAsync(seeded.Order.Id, false);

        Assert.Equal(OpenserveFulfilmentState.BlockedOrderData, view.State);
        Assert.False(view.ManualSubmission.Allowed);
        Assert.Contains("AMID", view.ManualSubmission.Reason);
        Assert.Empty(sent);
        Assert.Equal(OpenserveFulfilmentState.BlockedOrderData, forced.Data!.State);
        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveBlockedCodes.Amid, record.LastFailureCode);
    }

    // ─── 10 / 11. Admin pause / resume ──────────────────────────────

    [Fact]
    public async Task PausedOrder_IsNotSubmitted_ByAnyPath()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture, "Thandi", "Mokoena");
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()), userId: adminId);
        var fulfilment = Fulfilment(fixture.AppDbContext, submission, userId: adminId);

        var paused = await fulfilment.PauseAutomationAsync(seeded.Order.Id, "Customer asked us to hold the installation.");
        Assert.True(paused.IsSuccess, paused.Message);

        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        var manual = await fulfilment.SubmitAsync(seeded.Order.Id, false);
        var sweep = await Recovery(fixture.AppDbContext, submission).RunSafetySweepAsync();
        var view = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;

        Assert.Empty(sent);
        Assert.False(manual.IsSuccess);
        Assert.Contains("paused", manual.Message);
        Assert.Equal(0, sweep.Candidates);
        Assert.Equal(OpenserveFulfilmentState.BlockedAdmin, view.State);
        Assert.Equal("BLOCKED — ADMIN", view.StateLabel);
        Assert.True(view.Automation.Paused);
        Assert.Equal("Thandi Mokoena", view.Automation.PausedBy);
        Assert.Equal("Customer asked us to hold the installation.", view.Automation.Reason);
        Assert.False(view.ManualSubmission.Allowed);
        Assert.False(view.AutomaticRetry.Allowed);
    }

    [Fact]
    public async Task Resume_RestoresEligibility_AndBothActionsAreAudited()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()), userId: adminId);
        var fulfilment = Fulfilment(fixture.AppDbContext, submission, userId: adminId);

        await fulfilment.PauseAutomationAsync(seeded.Order.Id, "Hold");
        var resumed = await fulfilment.ResumeAutomationAsync(seeded.Order.Id, "Customer ready");

        Assert.True(resumed.IsSuccess, resumed.Message);
        Assert.False(resumed.Data!.Automation.Paused);
        Assert.True(resumed.Data.ManualSubmission.Allowed);
        var submitted = await fulfilment.SubmitAsync(seeded.Order.Id, false);
        Assert.True(submitted.Data!.ForwardedToOpenserve);
        Assert.Single(sent);

        var audits = await fixture.DbContext.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.Order && a.EntityId == seeded.Order.Id)
            .OrderBy(a => a.CreatedAtUtc).ToListAsync();
        Assert.Collection(audits.Where(a => a.ActionType is AuditActionType.OpenserveAutomationPaused or AuditActionType.OpenserveAutomationResumed),
            a => { Assert.Equal(AuditActionType.OpenserveAutomationPaused, a.ActionType); Assert.Equal(adminId, a.ActorUserId); Assert.Contains("Hold", a.MetadataJson); },
            a => { Assert.Equal(AuditActionType.OpenserveAutomationResumed, a.ActionType); Assert.Equal(adminId, a.ActorUserId); Assert.Contains("Customer ready", a.MetadataJson); });
    }

    [Fact]
    public async Task PausingAnOrderOpenserveAlreadyHas_IsRefused_ReconciliationIsUntouched()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        fixture.AppDbContext.OpenserveOrders.Add(SubmittedRecord(seeded.Order));
        await fixture.AppDbContext.SaveChangesAsync();
        var fulfilment = Fulfilment(fixture.AppDbContext, Submission(fixture.AppDbContext, Client(new List<OpenserveCreateOrderCommand>(), Accepted())));

        var view = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        var pause = await fulfilment.PauseAutomationAsync(seeded.Order.Id, "too late");

        Assert.False(view.Automation.CanPause);
        Assert.False(pause.IsSuccess);
    }

    // ─── 12 / 13 / 14 / 15 / 16. Recovery worker selection ──────────

    [Fact]
    public async Task RetryableTransientFailure_IsResentByTheRecoveryWorker_OnlyWhenDue()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, ConnectionFailed(), Accepted());
        var submission = Submission(fixture.AppDbContext, client);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        var failed = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveSubmissionFailureClass.Retryable, failed.LastFailureClass);
        Assert.InRange(failed.NextAutomaticRetryAtUtc!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));

        var recovery = Recovery(fixture.AppDbContext, submission);
        var notYet = await recovery.RunRetryPassAsync();
        Assert.Equal(0, notYet.Candidates);
        Assert.Single(sent);

        await MakeRetryDueAsync(fixture, seeded.Order.Id);
        var due = await recovery.RunRetryPassAsync();

        Assert.Equal(1, due.Submitted);
        Assert.Equal(2, sent.Count);
        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Submitted, record.NormalizedStatus);
        Assert.Equal(1, record.AutomaticRetryCount);
        Assert.Equal(OpenserveSubmissionTrigger.BackgroundRetry, record.LastSubmissionTrigger);
    }

    [Fact]
    public async Task AutomaticRetries_BackOff_AndStopAtMaxAttempts()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var settings = Settings(recovery: r => { r.MaxAttempts = 2; r.BaseRetryDelayMinutes = 15; });
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, ServiceUnavailable()), settings);
        var recovery = Recovery(fixture.AppDbContext, submission, settings);

        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        await MakeRetryDueAsync(fixture, seeded.Order.Id);
        await recovery.RunRetryPassAsync();
        var afterFirstRetry = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(1, afterFirstRetry.AutomaticRetryCount);
        Assert.InRange(afterFirstRetry.NextAutomaticRetryAtUtc!.Value, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31)); // 15 → 30

        await MakeRetryDueAsync(fixture, seeded.Order.Id);
        await recovery.RunRetryPassAsync();
        var exhausted = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(2, exhausted.AutomaticRetryCount);
        Assert.Null(exhausted.NextAutomaticRetryAtUtc);

        var further = await recovery.RunRetryPassAsync();
        Assert.Equal(0, further.Candidates);
        Assert.Equal(3, sent.Count);

        var view = (await Fulfilment(fixture.AppDbContext, submission, settings).GetAsync(seeded.Order.Id)).Data!;
        Assert.False(view.AutomaticRetry.Allowed);
        Assert.Contains("used up", view.AutomaticRetry.Reason);
        Assert.True(view.ManualSubmission.Allowed);
    }

    [Theory]
    [InlineData(400, "GEN-45994", OpenserveSubmissionFailureClass.NonRetryable)]
    [InlineData(401, "Unauthorized", OpenserveSubmissionFailureClass.NonRetryable)]
    [InlineData(200, "E84119", OpenserveSubmissionFailureClass.NonRetryable)]
    [InlineData(500, "InternalServerError", OpenserveSubmissionFailureClass.OutcomeUnknown)]
    [InlineData(502, "BadGateway", OpenserveSubmissionFailureClass.OutcomeUnknown)]
    [InlineData(504, "GatewayTimeout", OpenserveSubmissionFailureClass.OutcomeUnknown)]
    [InlineData(200, "PARSE_ERROR", OpenserveSubmissionFailureClass.OutcomeUnknown)]
    [InlineData(null, "TIMEOUT", OpenserveSubmissionFailureClass.OutcomeUnknown)]
    [InlineData(null, "TRANSPORT_ERROR", OpenserveSubmissionFailureClass.OutcomeUnknown)]
    public async Task NonRetryableOrOutcomeUnknownFailures_AreNeverResentAutomatically(int? status, string code, OpenserveSubmissionFailureClass expected)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Failed(status, code, "Openserve said no.")));
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);

        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(expected, record.LastFailureClass);
        Assert.Null(record.NextAutomaticRetryAtUtc);

        // Even with a due time forced on, the worker only ever resends Retryable.
        await MakeRetryDueAsync(fixture, seeded.Order.Id);
        var recovery = Recovery(fixture.AppDbContext, submission);
        await recovery.RunRetryPassAsync();
        await recovery.RunSafetySweepAsync();
        Assert.Single(sent);
    }

    [Theory]
    [InlineData(null, "CONNECTION_FAILED", OpenserveSubmissionFailureClass.Retryable)]
    [InlineData(503, "ServiceUnavailable", OpenserveSubmissionFailureClass.Retryable)]
    [InlineData(429, "TooManyRequests", OpenserveSubmissionFailureClass.Retryable)]
    [InlineData(408, "RequestTimeout", OpenserveSubmissionFailureClass.Retryable)]
    [InlineData(403, "Forbidden", OpenserveSubmissionFailureClass.NonRetryable)]
    [InlineData(422, "E1", OpenserveSubmissionFailureClass.NonRetryable)]
    [InlineData(null, "INTERRUPTED", OpenserveSubmissionFailureClass.OutcomeUnknown)]
    [InlineData(null, "SOMETHING_NEW", OpenserveSubmissionFailureClass.OutcomeUnknown)]
    public void Classifier_OnlyCallsSomethingRetryable_WhenOpenserveCannotHaveProcessedIt(int? status, string code, OpenserveSubmissionFailureClass expected)
        => Assert.Equal(expected, OpenserveSubmissionFailureClassifier.Classify(status, code).Class);

    [Fact]
    public async Task OutcomeUnknown_ManualRetryRequiresExplicitConfirmation()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Timeout(), Accepted()), userId: adminId);
        var fulfilment = Fulfilment(fixture.AppDbContext, submission, userId: adminId);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);

        var view = (await fulfilment.GetAsync(seeded.Order.Id)).Data!;
        Assert.Equal(OpenserveFulfilmentState.FailedOutcomeUnknown, view.State);
        Assert.True(view.ManualSubmission.RequiresOutcomeConfirmation);
        Assert.False(view.AutomaticRetry.Allowed);

        var unconfirmed = await fulfilment.SubmitAsync(seeded.Order.Id, confirmOutcomeUnknown: false);
        Assert.Equal(ErrorCodes.CONFLICT, unconfirmed.Code);
        Assert.Single(sent);

        var confirmed = await fulfilment.SubmitAsync(seeded.Order.Id, confirmOutcomeUnknown: true);
        Assert.True(confirmed.Data!.ForwardedToOpenserve);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task InterruptedSubmission_IsRecordedAsOutcomeUnknown_AndNeverResentAutomatically()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        fixture.AppDbContext.OpenserveOrders.Add(new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = seeded.Order.Id, ExternalReferenceNumber = $"SF-{seeded.Order.OrderNumber}", OrderType = OpenserveSubmissionRules.SalesOrderType,
            NormalizedStatus = OpenserveProvisioningStatus.Submitting, LastSubmissionAttemptAtUtc = DateTime.UtcNow.AddMinutes(-45),
            LastSubmissionTrigger = OpenserveSubmissionTrigger.AutomaticInitial, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-45)
        });
        await fixture.AppDbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()));

        var pass = await Recovery(fixture.AppDbContext, submission).RunRetryPassAsync();

        Assert.Equal(1, pass.InterruptedResolved);
        Assert.Empty(sent);
        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Failed, record.NormalizedStatus);
        Assert.Equal(OpenserveSubmissionFailureClass.OutcomeUnknown, record.LastFailureClass);
        Assert.Equal(OpenserveApiErrorCodes.Interrupted, record.LastFailureCode);
        Assert.True(await fixture.DbContext.OpenserveIntegrationLogs.AnyAsync(l => l.OpenserveOrderId == record.Id && l.ErrorSummary!.StartsWith("INTERRUPTED")));
    }

    [Fact]
    public async Task CancelledOrder_IsNotRetriedAutomatically()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, ServiceUnavailable(), Accepted()));
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);

        await fixture.DbContext.Orders.Where(o => o.Id == seeded.Order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Cancelled));
        await MakeRetryDueAsync(fixture, seeded.Order.Id);
        var pass = await Recovery(fixture.AppDbContext, submission).RunRetryPassAsync();

        Assert.Equal(0, pass.Candidates);
        Assert.Single(sent);
    }

    [Fact]
    public async Task PausedOrder_IsNotRetriedAutomatically()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, ServiceUnavailable(), Accepted()), userId: adminId);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        await Fulfilment(fixture.AppDbContext, submission, userId: adminId).PauseAutomationAsync(seeded.Order.Id, "hold");

        await MakeRetryDueAsync(fixture, seeded.Order.Id);
        var pass = await Recovery(fixture.AppDbContext, submission).RunRetryPassAsync();

        Assert.Equal(0, pass.Candidates);
        Assert.Single(sent);
    }

    [Fact]
    public async Task SuccessfulOpenserveOrder_IsNeverSelectedBySubmissionRecovery()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var record = SubmittedRecord(seeded.Order);
        // Even inconsistent leftovers (Retryable class + a due time) must not matter once Openserve has the order.
        record.LastFailureClass = OpenserveSubmissionFailureClass.Retryable;
        record.NextAutomaticRetryAtUtc = DateTime.UtcNow.AddHours(-1);
        fixture.AppDbContext.OpenserveOrders.Add(record);
        await fixture.AppDbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        var recovery = Recovery(fixture.AppDbContext, Submission(fixture.AppDbContext, Client(sent, Accepted())));

        var retry = await recovery.RunRetryPassAsync();
        var sweep = await recovery.RunSafetySweepAsync();

        Assert.Equal(0, retry.Candidates);
        Assert.Equal(0, sweep.Candidates);
        Assert.Empty(sent);
    }

    // ─── 17. Reconciliation never resubmits ─────────────────────────

    [Fact]
    public async Task ReconciliationFailure_NeverTriggersCreateOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        fixture.AppDbContext.OpenserveOrders.Add(SubmittedRecord(seeded.Order));
        await fixture.AppDbContext.SaveChangesAsync();

        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.GetOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveGetOrderOutcome>.Failure(Guid.NewGuid().ToString(), "GET", "getproductorder", null, "", null, OpenserveApiErrorCodes.Timeout, "Openserve request timed out."));
        var reconciliation = new OpenserveReconciliationService(fixture.AppDbContext, client.Object, Mock.Of<IOpenserveOrderUpdatePipeline>(),
            Config(Settings()), Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(), NullLogger<OpenserveReconciliationService>.Instance);
        var submission = Submission(fixture.AppDbContext, client);

        await reconciliation.ReconcileNonTerminalOrdersAsync();
        await reconciliation.ReconcileNonTerminalOrdersAsync();
        var recovery = Recovery(fixture.AppDbContext, submission);
        await recovery.RunRetryPassAsync();
        await recovery.RunSafetySweepAsync();

        client.Verify(c => c.GetOrderAsync("1742148", It.IsAny<CancellationToken>()), Times.Exactly(2));
        client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Submitted, record.NormalizedStatus);
    }

    // ─── 18. Concurrency ─────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentRetries_CannotProduceASecondOpenserveOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture);
        await Submission(fixture.AppDbContext, Client(new List<OpenserveCreateOrderCommand>(), ServiceUnavailable())).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        await MakeRetryDueAsync(fixture, seeded.Order.Id);

        // Attempt A (Admin double-click #1) is mid-flight inside the Openserve call…
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCalls = 0;
        var slowClient = new Mock<IOpenserveApiClient>();
        slowClient.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async (OpenserveCreateOrderCommand _, CancellationToken __) =>
            {
                Interlocked.Increment(ref slowCalls);
                started.TrySetResult();
                await release.Task;
                return Accepted();
            });
        var attemptA = Submission(fixture.AppDbContext, slowClient, userId: adminId).SubmitAsync(new OpenserveSubmissionRequest(seeded.Order.Id, OpenserveSubmissionTrigger.AdminManual));
        await started.Task;

        // …while a second click, the background worker and the sweep all try on another DbContext.
        await using var otherDb = fixture.CreateSiblingContext();
        var otherSent = new List<OpenserveCreateOrderCommand>();
        var other = Submission(otherDb, Client(otherSent, Accepted("9999999")), userId: adminId);
        var secondClick = await other.SubmitAsync(new OpenserveSubmissionRequest(seeded.Order.Id, OpenserveSubmissionTrigger.AdminManual) { ConfirmOutcomeUnknown = true });
        var background = await other.SubmitAsync(new OpenserveSubmissionRequest(seeded.Order.Id, OpenserveSubmissionTrigger.BackgroundRetry));
        var sweep = await Recovery(otherDb, other).RunSafetySweepAsync();

        release.SetResult();
        var resultA = await attemptA;

        Assert.True(resultA.IsSuccess, resultA.Message);
        Assert.Equal(OpenserveSubmissionOutcome.Submitted, resultA.Data!.Outcome);
        Assert.Equal(ErrorCodes.CONFLICT, secondClick.Code);
        Assert.Contains("in progress", secondClick.Message);
        Assert.False(background.IsSuccess);
        Assert.Equal(0, sweep.Candidates);
        Assert.Empty(otherSent);
        Assert.Equal(1, slowCalls);
        Assert.Equal(1, await fixture.DbContext.OpenserveOrders.CountAsync(o => o.OrderId == seeded.Order.Id));
    }

    [Fact]
    public async Task ConcurrentFirstSubmissions_CannotCreateTwoRecordsForOneOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);

        // The automatic trigger is mid-flight inside the Openserve call…
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowClient = new Mock<IOpenserveApiClient>();
        slowClient.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async (OpenserveCreateOrderCommand _, CancellationToken __) => { started.TrySetResult(); await release.Task; return Accepted(); });
        var automatic = Submission(fixture.AppDbContext, slowClient).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        await started.Task;

        // …and the sweep / an Admin Send arrive on another DbContext.
        await using var otherDb = fixture.CreateSiblingContext();
        var otherSent = new List<OpenserveCreateOrderCommand>();
        var other = Submission(otherDb, Client(otherSent, Accepted("9999999")));
        var sweepAttempt = await other.SubmitAsync(new OpenserveSubmissionRequest(seeded.Order.Id, OpenserveSubmissionTrigger.SafetySweep));
        var adminAttempt = await other.SubmitAsync(new OpenserveSubmissionRequest(seeded.Order.Id, OpenserveSubmissionTrigger.AdminManual));

        release.SetResult();
        await automatic;

        Assert.Equal(ErrorCodes.CONFLICT, sweepAttempt.Code);
        Assert.Equal(ErrorCodes.CONFLICT, adminAttempt.Code);
        Assert.Empty(otherSent);
        Assert.Equal(1, await fixture.DbContext.OpenserveOrders.CountAsync(o => o.OrderId == seeded.Order.Id));

        // And the database itself refuses a second record for the same order
        // (unique External Reference Number) — the guard behind the insert race.
        await using var raceDb = fixture.CreateSiblingContext();
        raceDb.OpenserveOrders.Add(new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = seeded.Order.Id, ExternalReferenceNumber = $"SF-{seeded.Order.OrderNumber}", OrderType = OpenserveSubmissionRules.SalesOrderType,
            NormalizedStatus = OpenserveProvisioningStatus.Submitting, CreatedAtUtc = DateTime.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => raceDb.SaveChangesAsync());
    }

    [Fact]
    public async Task AtomicClaim_FromOneValidatedSnapshot_HasExactlyOneWinner_AndStaleSnapshotsLose()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        await Submission(fixture.AppDbContext, Client(new List<OpenserveCreateOrderCommand>(), ServiceUnavailable())).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        var snapshot = await RecordAsync(fixture, seeded.Order.Id); // what two racing callers both validated

        await using var otherDb = fixture.CreateSiblingContext();
        var first = await OpenserveSubmissionClaim.TryClaimAsync(fixture.AppDbContext, snapshot, OpenserveSubmissionTrigger.AdminManual, DateTime.UtcNow);
        var second = await OpenserveSubmissionClaim.TryClaimAsync(otherDb, snapshot, OpenserveSubmissionTrigger.BackgroundRetry, DateTime.UtcNow);
        Assert.True(first);
        Assert.False(second);

        // A snapshot taken before the record changed (e.g. it became OutcomeUnknown) can't claim it either.
        await fixture.DbContext.OpenserveOrders.Where(o => o.Id == snapshot.Id).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.NormalizedStatus, OpenserveProvisioningStatus.Failed)
            .SetProperty(o => o.LastFailureClass, OpenserveSubmissionFailureClass.OutcomeUnknown));
        Assert.False(await OpenserveSubmissionClaim.TryClaimAsync(otherDb, snapshot, OpenserveSubmissionTrigger.BackgroundRetry, DateTime.UtcNow));

        // And nothing can claim a record Openserve accepted.
        var accepted = await RecordAsync(fixture, seeded.Order.Id);
        await fixture.DbContext.OpenserveOrders.Where(o => o.Id == snapshot.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.OpenserveOrderId, "1742148"));
        Assert.False(await OpenserveSubmissionClaim.TryClaimAsync(otherDb, accepted, OpenserveSubmissionTrigger.AdminManual, DateTime.UtcNow));
    }

    // ─── 21 / 22. Safety sweep ───────────────────────────────────────

    [Fact]
    public async Task SafetySweep_SubmitsAnEligibleFibreOrderThatHasNoRecord()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture); // automatic trigger never ran for it
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()));

        var view = (await Fulfilment(fixture.AppDbContext, submission).GetAsync(seeded.Order.Id)).Data!;
        Assert.True(view.AutomaticRetry.Allowed, view.AutomaticRetry.Reason);

        var sweep = await Recovery(fixture.AppDbContext, submission).RunSafetySweepAsync();

        Assert.Equal(1, sweep.Candidates);
        Assert.Equal(1, sweep.Submitted);
        var command = Assert.Single(sent);
        Assert.Equal($"SF-{seeded.Order.OrderNumber}", command.ExternalReferenceNumber);
        var record = await RecordAsync(fixture, seeded.Order.Id);
        Assert.Equal(OpenserveSubmissionTrigger.SafetySweep, record.LastSubmissionTrigger);
        Assert.Equal(OpenserveProvisioningStatus.Submitted, record.NormalizedStatus);
    }

    [Fact]
    public async Task SafetySweep_IgnoresHistoricalAndIneligibleOrders()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var now = DateTime.UtcNow;
        var settings = Settings(recovery: r => { r.SafetySweepLookbackDays = 7; r.SafetySweepGraceMinutes = 15; });

        var alreadyInstalled = await SeedAsync(fixture, status: OrderStatus.Active, accountStatus: NetworkAccountStatus.Active);
        var pendingActivation = await SeedAsync(fixture, status: OrderStatus.PendingActivation);
        var cancelled = await SeedAsync(fixture, status: OrderStatus.Cancelled);
        var unpaid = await SeedAsync(fixture, status: OrderStatus.AwaitingPayment);
        var tooOld = await SeedAsync(fixture, accountCreatedAtUtc: now.AddDays(-10));
        var tooNew = await SeedAsync(fixture, accountCreatedAtUtc: now.AddMinutes(-2));
        var notFibre = await SeedAsync(fixture, type: ServicePackageType.Security);
        var noAccount = await SeedAsync(fixture, withAccount: false);
        var beforeEnable = await SeedAsync(fixture, accountCreatedAtUtc: now.AddHours(-3));
        var paused = await SeedAsync(fixture);
        var alreadyFailed = await SeedAsync(fixture);
        var eligible = await SeedAsync(fixture, accountCreatedAtUtc: now.AddMinutes(-40));

        // The integration was last switched on 2h ago — anything that became
        // eligible before that may have been ordered by hand.
        fixture.AppDbContext.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorType = AuditActorType.Admin, ActionType = AuditActionType.OpenserveIntegrationEnabled, EntityType = AuditEntityType.OpenserveIntegrationConfig,
            Summary = "Openserve integration enabled.", CreatedAtUtc = now.AddHours(-2)
        });
        await fixture.AppDbContext.Orders.Where(o => o.Id == paused.Order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.OpenserveAutomationPaused, true));
        fixture.AppDbContext.OpenserveOrders.Add(new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = alreadyFailed.Order.Id, ExternalReferenceNumber = $"SF-{alreadyFailed.Order.OrderNumber}", OrderType = OpenserveSubmissionRules.SalesOrderType,
            NormalizedStatus = OpenserveProvisioningStatus.Failed, LastFailureClass = OpenserveSubmissionFailureClass.NonRetryable, CreatedAtUtc = now.AddMinutes(-50)
        });
        await fixture.AppDbContext.SaveChangesAsync();

        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = Submission(fixture.AppDbContext, Client(sent, Accepted()), settings);
        var sweep = await Recovery(fixture.AppDbContext, submission, settings).RunSafetySweepAsync();

        Assert.Equal(1, sweep.Candidates);
        var command = Assert.Single(sent);
        Assert.Equal($"SF-{eligible.Order.OrderNumber}", command.ExternalReferenceNumber);
        foreach (var ignored in new[] { alreadyInstalled, pendingActivation, cancelled, unpaid, tooOld, tooNew, notFibre, noAccount, beforeEnable, paused })
            Assert.False(await fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == ignored.Order.Id), ignored.Order.OrderNumber);

        var beforeEnableView = (await Fulfilment(fixture.AppDbContext, submission, settings).GetAsync(beforeEnable.Order.Id)).Data!;
        Assert.False(beforeEnableView.AutomaticRetry.Allowed);
        Assert.True(beforeEnableView.ManualSubmission.Allowed); // Admin can still send it deliberately
    }

    [Fact]
    public async Task SafetySweep_RespectsTheConfiguredNotBeforeFloor()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, accountCreatedAtUtc: DateTime.UtcNow.AddHours(-5));
        var settings = Settings(recovery: r => r.SafetySweepNotBeforeUtc = DateTime.UtcNow.AddHours(-1));
        var sent = new List<OpenserveCreateOrderCommand>();

        var sweep = await Recovery(fixture.AppDbContext, Submission(fixture.AppDbContext, Client(sent, Accepted()), settings), settings).RunSafetySweepAsync();

        Assert.Equal(0, sweep.Candidates);
        Assert.Empty(sent);
        Assert.False(await fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == seeded.Order.Id));
    }

    // ─── 23. Activity history ────────────────────────────────────────

    [Fact]
    public async Task ActivityHistory_ShowsAutomaticManualAndBackgroundAttempts()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture, "Sipho", "Dlamini");
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, ConnectionFailed(), ServiceUnavailable(), Accepted());
        var systemSubmission = Submission(fixture.AppDbContext, client);
        var adminSubmission = Submission(fixture.AppDbContext, client, userId: adminId);
        var fulfilment = Fulfilment(fixture.AppDbContext, adminSubmission, userId: adminId);

        await fulfilment.PauseAutomationAsync(seeded.Order.Id, "Checking the address with the customer");
        await fulfilment.ResumeAutomationAsync(seeded.Order.Id, null);
        await systemSubmission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);   // automatic → connection failed
        await MakeRetryDueAsync(fixture, seeded.Order.Id);
        await Recovery(fixture.AppDbContext, systemSubmission).RunRetryPassAsync();            // background → 503
        await fulfilment.SubmitAsync(seeded.Order.Id, false);                                   // Admin → accepted

        var activity = (await fulfilment.GetAsync(seeded.Order.Id)).Data!.Activity;
        var titles = activity.Select(a => a.Title).ToList();

        Assert.Contains("Automatic submission failed", titles);
        Assert.Contains("Automatic retry failed", titles);
        Assert.Contains("Admin submission — accepted by Openserve", titles);
        Assert.Contains("Openserve status: Validated", titles);
        Assert.Contains("Openserve automation paused by Sipho Dlamini", titles);
        Assert.Contains("Openserve automation resumed by Sipho Dlamini", titles);
        Assert.Equal(activity.OrderByDescending(a => a.OccurredAtUtc).Select(a => a.Title), titles); // newest first
        Assert.Equal("Checking the address with the customer", activity.Single(a => a.Title.StartsWith("Openserve automation paused")).Detail);
    }

    // ─── 24 / 25 / 26. Access, secrets, customer view ────────────────

    [Fact]
    public void OrderDetailOpenserveEndpoints_AreAdminOnly()
    {
        var controller = typeof(SmartFuture.API.Controllers.OpenserveOrdersController);
        var authorize = controller.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal(SmartFuture.Shared.Constants.AuthorizationPolicies.RequireAdmin, authorize!.Policy);

        foreach (var action in new[] { "GetFulfilment", "Submit", "PauseAutomation", "ResumeAutomation", "Retry" })
        {
            var method = controller.GetMethod(action);
            Assert.NotNull(method);
            Assert.Null(method!.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        }
    }

    [Fact]
    public async Task NoCredentialsLeak_IntoTheFulfilmentDto_OrTheLogs()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var adminId = await AdminAsync(fixture);
        var submissionLogger = new CapturingLogger<OpenserveOrderSubmissionService>();
        var recoveryLogger = new CapturingLogger<OpenserveSubmissionRecoveryService>();
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, ServiceUnavailable(), Timeout());
        var submission = Submission(fixture.AppDbContext, client, userId: adminId, logger: submissionLogger);
        await submission.TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);
        await MakeRetryDueAsync(fixture, seeded.Order.Id);
        await Recovery(fixture.AppDbContext, submission, logger: recoveryLogger).RunRetryPassAsync();

        var dto = (await Fulfilment(fixture.AppDbContext, submission, userId: adminId).GetAsync(seeded.Order.Id)).Data!;
        var json = JsonSerializer.Serialize(dto);

        Assert.DoesNotContain(FakeApiKey, json);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
        var forbidden = new Regex("api.?key|secret|password|token|header|requestbody|responsebody", RegexOptions.IgnoreCase);
        foreach (var type in new[] { typeof(OpenserveOrderFulfilmentDto), typeof(OpenserveFulfilmentActivityDto), typeof(OpenserveManualSubmissionDto), typeof(OpenserveAutomationPauseStateDto) })
            Assert.DoesNotContain(type.GetProperties(), p => forbidden.IsMatch(p.Name));
        Assert.DoesNotContain(submissionLogger.Messages, m => m.Contains(FakeApiKey));
        Assert.DoesNotContain(recoveryLogger.Messages, m => m.Contains(FakeApiKey));
        Assert.DoesNotContain(await fixture.DbContext.OpenserveIntegrationLogs.AsNoTracking().Select(l => l.ErrorSummary ?? "").ToListAsync(), s => s.Contains(FakeApiKey));
    }

    [Fact]
    public async Task CustomerOrderDto_NeverExposesInternalOpenserveErrors()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await SeedAsync(fixture);
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, Failed(502, "BadGateway", "Openserve returned HTTP 502: Mashery gateway timeout"));
        await Submission(fixture.AppDbContext, client).TrySubmitForOrderAsync(seeded.Order.Id, seeded.Account!.Id);

        var orderService = new OrderService(fixture.AppDbContext, Mock.Of<IAuditService>(), CurrentUser(seeded.Order.UserId), Mock.Of<INotificationService>(), Mock.Of<INetworkAccountService>(),
            Mock.Of<IInstallationService>(), Mock.Of<ICoverageCheckService>(), Mock.Of<IOpenserveQualificationService>(), Options.Create(new PaymentSettings()), Options.Create(new BillingSettings()),
            NullLogger<OrderService>.Instance);

        var mine = await orderService.GetMineByIdAsync(seeded.Order.Id);

        Assert.True(mine.IsSuccess, mine.Message);
        Assert.Null(mine.Data!.OpenserveAdmin);
        Assert.Equal("Order delayed", mine.Data.Openserve!.FriendlyStatus);
        var json = JsonSerializer.Serialize(mine.Data);
        foreach (var leak in new[] { "Mashery", "HTTP 502", "BadGateway", "api_key", FakeApiKey, "OFC", "LastFailure", "MessageId", "Retryable", "OutcomeUnknown" })
            Assert.DoesNotContain(leak, json);

        var forbidden = new Regex("failure|error|message|sku|mapping|amid|trigger|retry|class", RegexOptions.IgnoreCase);
        Assert.DoesNotContain(typeof(OrderOpenserveSummaryDto).GetProperties(), p => forbidden.IsMatch(p.Name));
        Assert.DoesNotContain(typeof(OrderDto).GetProperties(), p => p.PropertyType == typeof(OpenserveOrderFulfilmentDto));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}

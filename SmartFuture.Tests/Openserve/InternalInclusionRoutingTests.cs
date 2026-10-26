using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Coverage.Providers;
using SmartFuture.Application.Installations;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.ServicePackages;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Domain.Coverage;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Coverage;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Billing;
using SmartFuture.Tests.Infrastructure;
using F = SmartFuture.Tests.Openserve.OpenserveEvidenceFixtures;
using H = SmartFuture.Tests.Openserve.OpenserveQualificationOrchestrationTests;

namespace SmartFuture.Tests.Openserve;

// PRODUCTION SAFETY: an order that qualified through SmartFuture's OWN coverage
// inclusion list (an active Coverage Map Include rule) is SmartFuture's to
// fulfil and must never reach Openserve — not from the post-payment trigger,
// the retry worker, the safety sweep or an Admin. Only an order whose persisted
// FulfilmentRoute is Openserve is ever sent; "Fibre" or "paid" is never proof.
//
// Precedence under test (business decision 2026-10-25, per package):
//   Exclude rules win → Openserve Product Qualification first → the inclusion
//   list as the fallback for whatever Openserve can't order at the address.
//   With the integration off, an Include rule decides as it always has.
//
// Everything runs through the REAL coverage check, checkout (OrderService),
// qualification, submission, network-account, recovery and payment-first
// conversion services on SQLite. The Openserve client is a mock that records
// every Create Order — production Openserve is never called.
public class InternalInclusionRoutingTests
{
    // Openserve's scripted answer for an AMID query is "61 OAK AVE RANDBURG",
    // Fibre Working with every mapped SKU (H.Qualified).
    private const decimal RandburgLat = -26.095950m;
    private const decimal RandburgLon = 27.927632m;
    // Giyani: SmartFuture's own network. Openserve lists no record there.
    private const decimal GiyaniLat = -23.302500m;
    private const decimal GiyaniLon = 30.718900m;

    private sealed class Harness
    {
        public required SqliteTestDbFixture Fixture { get; init; }
        public required IAppDbContext Db { get; init; }
        public required OpenserveFulfilmentSettings Settings { get; init; }
        public required Mock<IOpenserveApiClient> Client { get; init; }
        public required List<OpenserveCreateOrderCommand> Sent { get; init; }
        public required OpenserveQualificationService Qualification { get; init; }
        public required CoverageCheckService Coverage { get; init; }
        public required Mock<IFibreCoverageProvider> Gis { get; init; }
        public required OpenserveOrderSubmissionService Submission { get; init; }
        public required NetworkAccountService Accounts { get; init; }
        public required OpenserveSubmissionRecoveryService Recovery { get; init; }
        public required ServicePackage Fibre { get; init; }
        public ServicePackage? Unmapped { get; init; }

        public OrderService OrdersFor(Guid userId) =>
            new(Db, Mock.Of<IAuditService>(), H.CurrentUser(userId), Mock.Of<INotificationService>(), Mock.Of<INetworkAccountService>(), Mock.Of<IInstallationService>(),
                Mock.Of<ICoverageCheckService>(), Qualification, Options.Create(new PaymentSettings()), Options.Create(new BillingSettings()), NullLogger<OrderService>.Instance);

        public Guid NewCustomer() => TestEntityFactory.CreateUser(Db, $"incl-{Guid.NewGuid():N}@example.com", "Thandi", "Mokoena").Id;
    }

    /// <param name="openserveListsTheAddress">True: FORCEVERIFY returns 61 OAK AVE RANDBURG (Fibre). False: Openserve lists no record at the pin.</param>
    private static async Task<Harness> HarnessAsync(SqliteTestDbFixture fixture, bool openserveListsTheAddress, bool integrationEnabled = true, bool withUnmappedPackage = false)
    {
        var db = fixture.AppDbContext;
        var fibre = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"SmartFuture Fibre 50 {Guid.NewGuid():N}");
        fibre.DownloadSpeedMbps = 50;
        ServicePackage? unmapped = null;
        if (withUnmappedPackage)
        {
            unmapped = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"SmartFuture Fibre 100 {Guid.NewGuid():N}");
            unmapped.DownloadSpeedMbps = 100;
        }
        await db.SaveChangesAsync();
        // Only the 50 Mbps package has an Openserve mapping — the 100 Mbps one can't be ordered through Openserve.
        db.PackageOpenserveMappings.Add(new PackageOpenserveMapping
        {
            ServicePackageId = fibre.Id, Sku = "OFC", Capacity = "50", CapacityUom = "Mbps", OpenserveProductName = "Openserve Fibre Connect", IsEnabled = true
        });
        await db.SaveChangesAsync();

        var settings = H.Settings(integrationEnabled);
        settings.SubmissionRecovery.SafetySweepGraceMinutes = 0;
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = openserveListsTheAddress ? H.Client(sent, H.Qualified()) : H.ClientWithVerify(sent, F.Verify(), H.Qualified());
        var qualification = H.Qualification(db, client, settings);

        var packageDtos = new[] { fibre, unmapped }.Where(p => p is not null).Select((p, i) => new ServicePackageDto
        {
            Id = p!.Id, Type = ServicePackageType.Fibre, Status = ServicePackageStatus.Active, Name = p.Name, Price = 499m + i * 200m, DownloadSpeedMbps = p.DownloadSpeedMbps, DisplayOrder = i + 1
        }).ToList();
        var packages = new Mock<IServicePackageService>();
        packages.Setup(x => x.SearchCustomerAsync(It.IsAny<ServicePackageFilterRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PagedResult<ServicePackageDto>>.Success(new PagedResult<ServicePackageDto>(packageDtos, 1, 100, packageDtos.Count)));

        var gis = new Mock<IFibreCoverageProvider>(MockBehavior.Strict);
        var coverage = new CoverageCheckService(Mock.Of<IGeocodingService>(), gis.Object, packages.Object, new CoverageMapRuleService(db, NullLogger<CoverageMapRuleService>.Instance),
            Mock.Of<IHostEnvironment>(), NullLogger<CoverageCheckService>.Instance, qualification);

        var submission = H.Submission(db, client, qualification, settings);
        var provisioner = new Mock<INetworkProvisioner>();
        provisioner.SetupGet(p => p.ProviderName).Returns("NoOp");
        var accounts = new NetworkAccountService(db, Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(), provisioner.Object, submission, NullLogger<NetworkAccountService>.Instance);
        var recovery = new OpenserveSubmissionRecoveryService(db, submission, H.Config(settings), NullLogger<OpenserveSubmissionRecoveryService>.Instance);

        return new Harness
        {
            Fixture = fixture, Db = db, Settings = settings, Client = client, Sent = sent, Qualification = qualification, Coverage = coverage, Gis = gis, Submission = submission,
            Accounts = accounts, Recovery = recovery, Fibre = fibre, Unmapped = unmapped
        };
    }

    private static async Task<CoverageMapRule> AddRuleAsync(IAppDbContext db, string matchText, CoverageMapRuleType type = CoverageMapRuleType.Include,
        CoverageAddressMatchComponent components = CoverageAddressMatchComponent.SafeAreaDefault)
    {
        var rule = new CoverageMapRule { Name = $"{type} {matchText}", MatchText = matchText, RuleType = type, AllowedComponents = components, IsActive = true };
        db.CoverageMapRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    private static CoverageCheckRequestDto GiyaniCheck() => new()
    {
        Latitude = GiyaniLat, Longitude = GiyaniLon, AddressLine1 = "12 Mopani Street", Suburb = "Section A", City = "Giyani", Province = "Limpopo",
        AddressText = "12 Mopani Street, Section A, Giyani"
    };

    private static CoverageCheckRequestDto RandburgCheck() => new()
    {
        Latitude = RandburgLat, Longitude = RandburgLon, AddressLine1 = "61 Oak Ave", City = "Randburg", Province = "Gauteng", AddressText = "61 Oak Ave, Randburg"
    };

    private static CreateOrderRequestDto GiyaniOrder(Guid packageId, string city = "Giyani", CoverageMatchFieldsDto? coverageMatch = null) => new()
    {
        ServicePackageId = packageId, FullName = "Thandi Mokoena", Email = "thandi@example.com", PhoneNumber = "0821234567", AddressLine1 = "12 Mopani Street", Suburb = "Section A",
        City = city, Province = "Limpopo", PostalCode = "0826", Latitude = GiyaniLat, Longitude = GiyaniLon, PropertyType = PropertyType.House, CoverageMatch = coverageMatch
    };

    private static CreateOrderRequestDto RandburgOrder(Guid packageId) => new()
    {
        ServicePackageId = packageId, FullName = "Jane Doe", Email = "jane@example.com", PhoneNumber = "0821234567", AddressLine1 = "61 Oak Ave", City = "Randburg", Province = "Gauteng",
        PostalCode = "2194", Latitude = RandburgLat, Longitude = RandburgLon, PropertyType = PropertyType.House
    };

    private static async Task<Guid> PlaceOrderAsync(Harness h, CreateOrderRequestDto request)
    {
        var created = await h.OrdersFor(h.NewCustomer()).CreateMineAsync(request);
        Assert.True(created.IsSuccess, created.Message);
        return created.Data!.Id;
    }

    /// <summary>Payment lands: the order is PaymentReceived and the payment hook reserves the network account — the exact point that fires the automatic Openserve trigger.</summary>
    private static async Task<NetworkAccount> PayAsync(Harness h, Guid orderId)
    {
        var order = await h.Db.Orders.FirstAsync(o => o.Id == orderId);
        order.Status = OrderStatus.PaymentReceived;
        await h.Db.SaveChangesAsync();
        var reserved = await h.Accounts.EnsurePendingForOrderAsync(orderId, NetworkAccountSource.SystemAutomated);
        Assert.True(reserved.IsSuccess, reserved.Message);
        return await h.Fixture.DbContext.NetworkAccounts.AsNoTracking().SingleAsync(n => n.OrderId == orderId);
    }

    /// <summary>A paid order created before routes were recorded (NotRecorded), fully eligible for Openserve, with a Pending network account.</summary>
    private static async Task<(Guid OrderId, NetworkAccount Account)> SeedLegacyPaidOrderAsync(Harness h, OrderFulfilmentRoute route = OrderFulfilmentRoute.NotRecorded)
    {
        var user = TestEntityFactory.CreateUser(h.Db, $"legacy-{Guid.NewGuid():N}@example.com");
        var order = TestEntityFactory.CreateOrder(h.Db, user, h.Fibre, status: OrderStatus.PaymentReceived, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        order.FulfilmentRoute = route;
        order.AddressLine1 = "61 Oak Ave";
        order.FullName = "Jane Doe";
        order.PhoneNumber = "0821234567";
        order.Latitude = RandburgLat;
        order.Longitude = RandburgLon;
        order.OpenserveAmId = H.Amid;
        await h.Db.SaveChangesAsync();
        await F.SeedEligibleEvidenceAsync(h.Db, order);
        var account = TestEntityFactory.CreateNetworkAccount(h.Db, order, status: NetworkAccountStatus.Pending);
        account.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30);
        await h.Db.SaveChangesAsync();
        return (order.Id, account);
    }

    private static void VerifyNoCreateOrder(Harness h)
    {
        Assert.Empty(h.Sent);
        h.Client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Task<Domain.Orders.Order> OrderAsync(Harness h, Guid orderId) => h.Fixture.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);

    // ═══ 1. Internal inclusion → coverage available ═════════════════════

    [Fact]
    public async Task IncludedAddress_OpenserveCannotServeIt_CoverageIsAvailableFromTheInclusionList()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: false);
        var rule = await AddRuleAsync(h.Db, "Giyani");

        var result = await h.Coverage.CheckAsync(GiyaniCheck());

        Assert.True(result.IsSuccess, result.Message);
        var dto = result.Data!;
        Assert.True(dto.CoverageAvailable);
        Assert.Equal(CoverageMatchSource.CoverageMapInclude, dto.MatchSource);
        Assert.Equal(rule.Id, dto.MatchedRuleId);
        Assert.Equal(new[] { h.Fibre.Id }, dto.AvailablePackages.Select(p => p.Id));
        // The historical inclusion shape: no Openserve address steps for the customer.
        Assert.False(dto.AddressVerificationRequired);
        Assert.False(dto.ServicePremisesSelectionRequired);
        Assert.Null(dto.ServicePremises);
        h.Gis.VerifyNoOtherCalls();
    }

    // ═══ 2. + 5. Internal inclusion → checkout allowed, no AMID required ═

    [Fact]
    public async Task IncludedAddress_CheckoutIsAllowed_AndTheOrderIsRoutedToSmartFuture_WithNoAmidOrOpenserveEvidence()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: false);
        var rule = await AddRuleAsync(h.Db, "Giyani");

        var orderId = await PlaceOrderAsync(h, GiyaniOrder(h.Fibre.Id));

        var order = await OrderAsync(h, orderId);
        Assert.Equal(OrderFulfilmentRoute.InternalInclusion, order.FulfilmentRoute);
        Assert.Equal(rule.Id, order.FulfilmentCoverageRuleId);
        Assert.Null(order.OpenserveAmId);
        Assert.Null(order.OpenserveQualificationResultId);
        Assert.Null(order.OpenserveQualifiedAtUtc);
        h.Client.Verify(c => c.QualifyAsync(It.Is<OpenserveQualificationQuery>(q => !string.IsNullOrWhiteSpace(q.Amid)), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══ 3. + 4. Internal inclusion → payment succeeds, NO Product Order ══

    [Fact]
    public async Task IncludedOrder_PaymentSucceeds_TheNetworkAccountIsReserved_AndNoOpenserveProductOrderIsEverCreated()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: false);
        await AddRuleAsync(h.Db, "Giyani");
        var orderId = await PlaceOrderAsync(h, GiyaniOrder(h.Fibre.Id));
        h.Client.Invocations.Clear();

        var account = await PayAsync(h, orderId);

        Assert.Equal(NetworkAccountStatus.Pending, account.Status);
        Assert.Equal(OrderStatus.PaymentReceived, (await OrderAsync(h, orderId)).Status);
        VerifyNoCreateOrder(h);
        Assert.False(await h.Fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == orderId), "not even a blocked submission record");
        // The submission path never qualified it either (no self-heal Product Qualification).
        h.Client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(OpenserveSubmissionTrigger.AutomaticInitial)]
    [InlineData(OpenserveSubmissionTrigger.SafetySweep)]
    [InlineData(OpenserveSubmissionTrigger.BackgroundRetry)]
    [InlineData(OpenserveSubmissionTrigger.AdminManual)]
    public async Task IncludedOrder_IsRefusedOnEverySubmissionPath_IncludingAnAdminSend(OpenserveSubmissionTrigger trigger)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: false);
        await AddRuleAsync(h.Db, "Giyani");
        var orderId = await PlaceOrderAsync(h, GiyaniOrder(h.Fibre.Id));
        var account = await PayAsync(h, orderId);

        var attempt = await h.Submission.SubmitAsync(new OpenserveSubmissionRequest(orderId, trigger) { NetworkAccountId = account.Id });

        Assert.False(attempt.IsSuccess);
        Assert.Equal(OpenserveSubmissionRules.InternalInclusionReason, attempt.Message);
        VerifyNoCreateOrder(h);
        Assert.False(await h.Fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == orderId));
    }

    [Fact]
    public async Task IncludedOrder_OrderDetail_SaysSmartFutureFulfilsIt_AndOffersNoOpenserveSend()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: false);
        await AddRuleAsync(h.Db, "Giyani");
        var orderId = await PlaceOrderAsync(h, GiyaniOrder(h.Fibre.Id));
        await PayAsync(h, orderId);

        var detail = await H.Fulfilment(h.Db, h.Submission, h.Qualification, h.Settings).GetAsync(orderId);

        Assert.True(detail.IsSuccess, detail.Message);
        Assert.Equal("NotApplicable", detail.Data!.State);
        Assert.Contains("NOT AN OPENSERVE ORDER", detail.Data.StateLabel);
        Assert.False(detail.Data.ManualSubmission.Allowed);
        Assert.False(detail.Data.AutomaticRetry.Allowed);
    }

    // ═══ 6. + 10. Background workers never pick up inclusion or unrouted orders ═

    [Fact]
    public async Task SafetySweep_SubmitsOnlyOpenserveRoutedOrders_NeverInclusionOrUnroutedPaidFibreOrders()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: false);
        await AddRuleAsync(h.Db, "Giyani");

        // An inclusion order, paid, network account Pending, no submission record.
        var included = await PlaceOrderAsync(h, GiyaniOrder(h.Fibre.Id));
        var includedOrder = await h.Db.Orders.FirstAsync(o => o.Id == included);
        includedOrder.Status = OrderStatus.PaymentReceived;
        var includedAccount = TestEntityFactory.CreateNetworkAccount(h.Db, includedOrder, status: NetworkAccountStatus.Pending);
        includedAccount.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30);
        await h.Db.SaveChangesAsync();
        // An existing paid Fibre order from before routes were recorded — fully eligible, never submitted.
        var (legacyId, _) = await SeedLegacyPaidOrderAsync(h);
        // The control: an Openserve-routed order the trigger missed.
        var (openserveId, _) = await SeedLegacyPaidOrderAsync(h, OrderFulfilmentRoute.Openserve);

        var sweep = await h.Recovery.RunSafetySweepAsync();

        Assert.Equal(1, sweep.Candidates);
        Assert.Equal(1, sweep.Submitted);
        var command = Assert.Single(h.Sent);
        Assert.Equal("smartfuture 60", command.ExternalReferenceNumber);
        Assert.Equal(openserveId, (await h.Fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync()).OrderId);
        Assert.False(await h.Fixture.DbContext.OpenserveOrders.AnyAsync(o => o.OrderId == included || o.OrderId == legacyId));
    }

    [Fact]
    public async Task RetryWorker_NeverResendsAnUnroutedOrdersRetryableFailure_ButStillRetriesOpenserveOrders()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: true);
        var (legacyId, legacyAccount) = await SeedLegacyPaidOrderAsync(h);
        var (openserveId, openserveAccount) = await SeedLegacyPaidOrderAsync(h, OrderFulfilmentRoute.Openserve);
        foreach (var (orderId, account) in new[] { (legacyId, legacyAccount), (openserveId, openserveAccount) })
        {
            var number = (await OrderAsync(h, orderId)).OrderNumber;
            h.Db.OpenserveOrders.Add(new OpenserveOrder
            {
                Id = Guid.NewGuid(), OrderId = orderId, ExternalReferenceNumber = $"SF-{number}", OrderType = OpenserveSubmissionRules.SalesOrderType,
                NormalizedStatus = OpenserveProvisioningStatus.Failed, LastFailureClass = OpenserveSubmissionFailureClass.Retryable, LastFailureCode = "CONNECTION_FAILED",
                NextAutomaticRetryAtUtc = DateTime.UtcNow.AddMinutes(-1), LastSubmissionAttemptAtUtc = DateTime.UtcNow.AddMinutes(-20), CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                SubscriberReferenceNumber = account.OpenserveSubscriberReferenceNumber
            });
        }
        await h.Db.SaveChangesAsync();

        var pass = await h.Recovery.RunRetryPassAsync();

        Assert.Equal(1, pass.Candidates);
        var command = Assert.Single(h.Sent);
        Assert.Equal($"SF-{(await OrderAsync(h, openserveId)).OrderNumber}", command.ExternalReferenceNumber);
        var legacyRecord = await h.Fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == legacyId);
        Assert.Equal(OpenserveProvisioningStatus.Failed, legacyRecord.NormalizedStatus); // untouched
    }

    [Fact]
    public async Task UnroutedLegacyOrder_IsNeverSubmittedAutomatically_ButAnAdminSendRecordsOpenserve_AndIsAudited()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: true);
        var (orderId, account) = await SeedLegacyPaidOrderAsync(h);

        var automatic = await h.Submission.SubmitAsync(new OpenserveSubmissionRequest(orderId, OpenserveSubmissionTrigger.AutomaticInitial) { NetworkAccountId = account.Id });
        Assert.False(automatic.IsSuccess);
        Assert.Equal(OpenserveSubmissionRules.RouteNotRecordedReason, automatic.Message);
        VerifyNoCreateOrder(h);

        var admin = await h.Submission.SubmitAsync(new OpenserveSubmissionRequest(orderId, OpenserveSubmissionTrigger.AdminManual));

        Assert.True(admin.IsSuccess, admin.Message);
        Assert.Equal(OpenserveSubmissionOutcome.Submitted, admin.Data!.Outcome);
        Assert.Single(h.Sent);
        Assert.Equal(OrderFulfilmentRoute.Openserve, (await OrderAsync(h, orderId)).FulfilmentRoute);
        Assert.True(await h.Fixture.DbContext.AuditLogs.AnyAsync(a => a.ActionType == AuditActionType.OpenserveFulfilmentRouteRecorded));
    }

    // ═══ 7. Openserve-qualified address → correct automatic submission ═══

    [Fact]
    public async Task OpenserveQualifiedAddress_IsRoutedToOpenserve_AndSubmittedAutomaticallyAfterPayment_Once()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: true);

        var coverage = await h.Coverage.CheckAsync(RandburgCheck());
        Assert.True(coverage.Data!.CoverageAvailable);
        Assert.Equal(CoverageMatchSource.OpenserveQualification, coverage.Data.MatchSource);

        var orderId = await PlaceOrderAsync(h, RandburgOrder(h.Fibre.Id));
        var order = await OrderAsync(h, orderId);
        Assert.Equal(OrderFulfilmentRoute.Openserve, order.FulfilmentRoute);
        Assert.Equal(H.Amid, order.OpenserveAmId);

        var account = await PayAsync(h, orderId);
        await h.Accounts.EnsurePendingForOrderAsync(orderId, NetworkAccountSource.SystemAutomated); // a replayed webhook
        await h.Recovery.RunSafetySweepAsync();                                                     // and the sweep

        var command = Assert.Single(h.Sent);
        Assert.Equal(H.Amid, command.Amid);
        Assert.Equal("smartfuture 60", command.ExternalReferenceNumber);
        Assert.Equal(account.Id, (await h.Fixture.DbContext.NetworkAccounts.AsNoTracking().SingleAsync(n => n.OrderId == orderId)).Id);
    }

    // ═══ 8. Address covered by both → documented precedence ═════════════

    [Fact]
    public async Task AddressCoveredByBoth_OpenserveWins_ForEveryPackageOpenserveCanOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: true);
        await AddRuleAsync(h.Db, "Randburg");

        var coverage = await h.Coverage.CheckAsync(RandburgCheck());
        var orderId = await PlaceOrderAsync(h, RandburgOrder(h.Fibre.Id));
        await PayAsync(h, orderId);

        Assert.Equal(CoverageMatchSource.OpenserveQualification, coverage.Data!.MatchSource);
        Assert.Equal(new[] { h.Fibre.Id }, coverage.Data.AvailablePackages.Select(p => p.Id));
        Assert.Null(coverage.Data.MatchedRuleId); // the Include rule added nothing Openserve couldn't order
        Assert.Equal(OrderFulfilmentRoute.Openserve, (await OrderAsync(h, orderId)).FulfilmentRoute);
        Assert.Single(h.Sent);
    }

    [Fact]
    public async Task AddressCoveredByBoth_APackageOpenserveCannotOrder_ComesFromTheInclusionList_AndIsNeverSent()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: true, withUnmappedPackage: true);
        var rule = await AddRuleAsync(h.Db, "Randburg");

        var coverage = await h.Coverage.CheckAsync(RandburgCheck());

        Assert.True(coverage.IsSuccess, coverage.Message);
        Assert.Equal(CoverageMatchSource.OpenserveQualification, coverage.Data!.MatchSource);
        Assert.Equal(new[] { h.Fibre.Id, h.Unmapped!.Id }, coverage.Data.AvailablePackages.Select(p => p.Id));
        Assert.Equal("Available in this area", coverage.Data.AvailablePackages.Single(p => p.Id == h.Unmapped.Id).MatchReason);
        Assert.Equal(rule.Id, coverage.Data.MatchedRuleId);

        var openserveOrder = await PlaceOrderAsync(h, RandburgOrder(h.Fibre.Id));
        var internalOrder = await PlaceOrderAsync(h, RandburgOrder(h.Unmapped.Id));
        Assert.Equal(OrderFulfilmentRoute.Openserve, (await OrderAsync(h, openserveOrder)).FulfilmentRoute);
        Assert.Equal(OrderFulfilmentRoute.InternalInclusion, (await OrderAsync(h, internalOrder)).FulfilmentRoute);

        await PayAsync(h, openserveOrder);
        await PayAsync(h, internalOrder);
        await h.Recovery.RunSafetySweepAsync();

        var command = Assert.Single(h.Sent);
        Assert.Equal("smartfuture 60", command.ExternalReferenceNumber); // the internal order never consumed a reference
    }

    [Fact]
    public async Task AnExcludeRule_WinsOverAnIncludeRule_AndOpenserveIsNotAsked()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: false);
        await AddRuleAsync(h.Db, "Giyani");
        await AddRuleAsync(h.Db, "Section A", CoverageMapRuleType.Exclude);

        var coverage = await h.Coverage.CheckAsync(GiyaniCheck());
        var checkout = await h.OrdersFor(h.NewCustomer()).CreateMineAsync(GiyaniOrder(h.Fibre.Id));

        Assert.False(coverage.Data!.CoverageAvailable);
        Assert.Equal(CoverageMatchSource.CoverageMapExclude, coverage.Data.MatchSource);
        Assert.False(checkout.IsSuccess, "an excluded area is never routed internally");
        VerifyNoCreateOrder(h);
    }

    // ═══ 9. Non-covered address → rejected ══════════════════════════════

    [Fact]
    public async Task AddressCoveredByNeither_IsRejected_AtCoverageAndAtCheckout()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: false);

        var coverage = await h.Coverage.CheckAsync(GiyaniCheck());
        var checkout = await h.OrdersFor(h.NewCustomer()).CreateMineAsync(GiyaniOrder(h.Fibre.Id));

        Assert.True(coverage.IsSuccess, coverage.Message);
        Assert.False(coverage.Data!.CoverageAvailable);
        Assert.Empty(coverage.Data.AvailablePackages);
        Assert.False(checkout.IsSuccess);
        Assert.NotNull(checkout.Code);
        Assert.False(await h.Fixture.DbContext.Orders.AnyAsync());
    }

    // ═══ Checkout re-evaluates the inclusion list on the coverage-check components ═

    [Fact]
    public async Task ATownOnlyIncludeRule_MatchesAtCheckout_WhenTheClientSendsTheCoverageMatchFields()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: false);
        await AddRuleAsync(h.Db, "Greater Giyani", components: CoverageAddressMatchComponent.Town);
        var match = new CoverageMatchFieldsDto { Town = "Greater Giyani Local Municipality", FormattedAddress = "12 Mopani Street, Siyandani, 0826, South Africa" };

        var withMatch = await h.OrdersFor(h.NewCustomer()).CreateMineAsync(GiyaniOrder(h.Fibre.Id, city: "Siyandani", coverageMatch: match));
        var withoutMatch = await h.OrdersFor(h.NewCustomer()).CreateMineAsync(GiyaniOrder(h.Fibre.Id, city: "Siyandani"));

        Assert.True(withMatch.IsSuccess, withMatch.Message);
        Assert.Equal(OrderFulfilmentRoute.InternalInclusion, (await OrderAsync(h, withMatch.Data!.Id)).FulfilmentRoute);
        Assert.False(withoutMatch.IsSuccess, "without the Town the rule can't match, and Openserve can't serve the address");
    }

    // ═══ Payment-first (Order and Pay) conversion ═══════════════════════

    [Fact]
    public async Task PaymentFirstIntent_RoutedInternally_ConvertsToAPaidInternalOrder_WithoutCallingOpenserve()
    {
        var (fx, _, intent, reference) = await OrderIntentServiceConvertTests.SeedPaidIntentAsync(packageType: ServicePackageType.Fibre);
        await using var fixtureScope = fx;
        intent.Latitude = GiyaniLat;
        intent.Longitude = GiyaniLon;
        intent.City = "Giyani";
        intent.FulfilmentRoute = OrderFulfilmentRoute.InternalInclusion; // decided at the intent's checkout gate
        await fx.DbContext.SaveChangesAsync();
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = H.Client(sent, H.Qualified());
        var qualification = H.Qualification(fx.AppDbContext, client);

        var converted = await OrderIntentServiceConvertTests.BuildService(fx, OrderIntentServiceConvertTests.LooseApplier(), openserveQualification: qualification)
            .ConvertIntentPaymentToPaidOrderAsync(reference, paidAtUtc: null, gatewayTransactionId: "GW-TX-INCL-1");

        Assert.True(converted.IsSuccess, converted.Message);
        var order = await fx.DbContext.Orders.SingleAsync(o => o.Id == converted.Data!.OrderId);
        Assert.Equal(OrderFulfilmentRoute.InternalInclusion, order.FulfilmentRoute);
        Assert.Null(order.OpenserveAmId);
        Assert.True(await fx.DbContext.Payments.AnyAsync(p => p.Invoice!.OrderId == order.Id), "the payment was recorded against the order");

        var account = TestEntityFactory.CreateNetworkAccount(fx.AppDbContext, order, status: NetworkAccountStatus.Pending);
        await fx.AppDbContext.SaveChangesAsync();
        await H.Submission(fx.AppDbContext, client, qualification).TrySubmitForOrderAsync(order.Id, account.Id);

        Assert.Empty(sent);
        client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══ Integration off: legacy behaviour, routes still recorded ════════

    [Fact]
    public async Task IntegrationDisabled_TheIncludeRuleStillDecides_AndRoutesAreRecorded_WithoutCallingOpenserve()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, openserveListsTheAddress: true, integrationEnabled: false);
        await AddRuleAsync(h.Db, "Giyani");

        var coverage = await h.Coverage.CheckAsync(GiyaniCheck());
        var included = await PlaceOrderAsync(h, GiyaniOrder(h.Fibre.Id));
        var legacyGis = await PlaceOrderAsync(h, RandburgOrder(h.Fibre.Id));

        Assert.Equal(CoverageMatchSource.CoverageMapInclude, coverage.Data!.MatchSource);
        Assert.Equal(OrderFulfilmentRoute.InternalInclusion, (await OrderAsync(h, included)).FulfilmentRoute);
        Assert.Equal(OrderFulfilmentRoute.Openserve, (await OrderAsync(h, legacyGis)).FulfilmentRoute);
        h.Client.VerifyNoOtherCalls();
    }
}

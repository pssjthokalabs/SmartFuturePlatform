using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Coverage.Providers;
using SmartFuture.Application.Installations;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Application.ServicePackages;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Coverage;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;
using Xunit;
using F = SmartFuture.Tests.Openserve.OpenserveEvidenceFixtures;
using H = SmartFuture.Tests.Openserve.OpenserveQualificationOrchestrationTests;

namespace SmartFuture.Tests.Openserve;

// UAT, 2026-10-05: "2 Palmas Street, Thorn Field Estate, Centurion". FORCEVERIFY
// listed 8 PALMAS ST (AMID 52782141, 32.77 m), 9 DE OVALLE BLV (52782226,
// 33.25 m) and 10 PALMAS ST (52782228, 33.42 m) — none is number 2, so nothing
// matches automatically (correct). Openserve's own site asks the visitor to
// pick the network record that is their property; SmartFuture now does the
// same: the CUSTOMER chooses and confirms the Openserve SERVICE PREMISES, which
// is qualified by AMID before any package is offered. Their INSTALLATION
// ADDRESS is never replaced by it. With the integration off, the legacy public
// coverage path is untouched.
public class OpenserveServicePremisesSelectionTests
{
    private const string Palmas8 = "52782141";
    private const string Ovalle9 = "52782226";
    private const string Palmas10 = "52782228";
    private const decimal PinLat = -25.866500m;
    private const decimal PinLon = 28.107100m;

    private static OpenserveApiCallResult<OpenserveQualificationOutcome> UatCandidates() => F.Verify(
        (Palmas8, "8 PALMAS ST MONAVONI X 6 CENTURION", 32.77m, -25.866217m, 28.106903m),
        (Ovalle9, "9 DE OVALLE BLV MONAVONI X 6 CENTURION", 33.25m, -25.866390m, 28.107060m),
        (Palmas10, "10 PALMAS ST MONAVONI X 6 CENTURION", 33.42m, -25.866120m, 28.106760m));

    /// <summary>8 PALMAS with Fibre (what Openserve's public site shows) — OFC and the default products up to 1000 Mbps.</summary>
    private static OpenserveApiCallResult<OpenserveQualificationOutcome> Palmas8WithFibre() =>
        F.Call(F.Facts(Palmas8, "8", "PALMAS", "ST", "MONAVONI X 6", "CENTURION", latitude: -25.866217m, longitude: 28.106903m));

    /// <summary>8 PALMAS as STAGING answered it in UAT: an AMID, no ftthInfrastructure at all.</summary>
    private static OpenserveApiCallResult<OpenserveQualificationOutcome> Palmas8AsStaging() => F.FromJson(F.UatNoFtthResponse);

    private static OpenserveApiCallResult<OpenserveQualificationOutcome> Palmas10WithFibre() =>
        F.Call(F.Facts(Palmas10, "10", "PALMAS", "ST", "MONAVONI X 6", "CENTURION", latitude: -25.866120m, longitude: 28.106760m));

    // ─── harness ────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required CoverageCheckService Coverage { get; init; }
        public required OpenserveQualificationService Qualification { get; init; }
        public required Mock<IOpenserveApiClient> Client { get; init; }
        public required Mock<IFibreCoverageProvider> Gis { get; init; }
        public required Mock<ICoverageMapRuleService> Map { get; init; }
        public required List<OpenserveCreateOrderCommand> Sent { get; init; }
        public required Guid CustomerId { get; init; }
        public required Guid OfcPackageId { get; init; }
        public required OpenserveFulfilmentSettings Settings { get; init; }
    }

    /// <summary>
    /// FORCEVERIFY answers with <paramref name="verify"/> (re-read on every call); an AMID query answers from <paramref name="byAmid"/>.
    /// Create Order is recorded, never "sent" anywhere.
    /// </summary>
    private static Mock<IOpenserveApiClient> Client(List<OpenserveCreateOrderCommand> sent, Func<OpenserveApiCallResult<OpenserveQualificationOutcome>> verify,
        IReadOnlyDictionary<string, Func<OpenserveApiCallResult<OpenserveQualificationOutcome>>> byAmid)
    {
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OpenserveQualificationQuery q, CancellationToken _) => q.ForceVerify ? verify() : byAmid[q.Amid!]());
        client.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OpenserveCreateOrderCommand command, CancellationToken _) =>
            {
                sent.Add(command);
                return OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Success(Guid.NewGuid().ToString(), "POST", "https://stapitrx.openserve.co.za/ws-marut/productorder", 200, "{}", "{}",
                    new OpenserveCreateOrderOutcome("1742148", "Validated", "Order received for processing. Order Id = 1742148. State = Validated"));
            });
        return client;
    }

    private static async Task<Harness> HarnessAsync(SqliteTestDbFixture fixture, OpenserveFulfilmentSettings? settings = null,
        Func<OpenserveApiCallResult<OpenserveQualificationOutcome>>? verify = null,
        IReadOnlyDictionary<string, Func<OpenserveApiCallResult<OpenserveQualificationOutcome>>>? byAmid = null)
    {
        var db = fixture.AppDbContext;
        var customer = TestEntityFactory.CreateUser(db, $"palmas-{Guid.NewGuid():N}@example.com", "Thandi", "Mokoena");
        var ofc = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: "SmartFuture Fibre 50/25");
        ofc.DownloadSpeedMbps = 50;
        await db.SaveChangesAsync();
        db.PackageOpenserveMappings.Add(new PackageOpenserveMapping
        {
            ServicePackageId = ofc.Id, Sku = "OFC", Capacity = "50", CapacityUom = "Mbps", OpenserveProductName = "Openserve Fibre Connect", IsEnabled = true
        });
        await db.SaveChangesAsync();

        settings ??= H.Settings();
        var sent = new List<OpenserveCreateOrderCommand>();
        var client = Client(sent, verify ?? UatCandidates, byAmid ?? new Dictionary<string, Func<OpenserveApiCallResult<OpenserveQualificationOutcome>>>
        {
            [Palmas8] = Palmas8WithFibre, [Palmas10] = Palmas10WithFibre
        });
        var qualification = H.Qualification(db, client, settings, customer.Id);

        var packages = new Mock<IServicePackageService>();
        packages.Setup(x => x.SearchCustomerAsync(It.IsAny<ServicePackageFilterRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PagedResult<ServicePackageDto>>.Success(new PagedResult<ServicePackageDto>(new List<ServicePackageDto>
            {
                new() { Id = ofc.Id, Type = ServicePackageType.Fibre, Status = ServicePackageStatus.Active, Name = ofc.Name, Price = 499m, DownloadSpeedMbps = 50, DisplayOrder = 1 }
            }, 1, 100, 1)));
        var map = new Mock<ICoverageMapRuleService>();
        map.Setup(x => x.TryEvaluateAsync(It.IsAny<CoverageCheckRequestDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CoverageMapEvaluationResult());
        var gis = new Mock<IFibreCoverageProvider>(MockBehavior.Strict);
        var coverage = new CoverageCheckService(Mock.Of<IGeocodingService>(), gis.Object, packages.Object, map.Object, Mock.Of<IHostEnvironment>(), NullLogger<CoverageCheckService>.Instance,
            qualification);

        return new Harness
        {
            Coverage = coverage, Qualification = qualification, Client = client, Gis = gis, Map = map, Sent = sent, CustomerId = customer.Id, OfcPackageId = ofc.Id, Settings = settings
        };
    }

    private static CoverageCheckRequestDto PalmasCheck() => new()
    {
        Latitude = PinLat, Longitude = PinLon, AddressLine1 = "2 Palmas Street", Suburb = "Thorn Field Estate", City = "Centurion", Province = "Gauteng",
        AddressText = "2 Palmas St, Thorn Field Estate, Centurion, 0157, South Africa"
    };

    private static CoverageServicePremisesSelectionRequestDto Choose(Guid reference, string key, bool confirmed = true) => new()
    {
        VerificationReference = reference, CandidateKey = key, Confirmed = confirmed, AddressLine1 = "2 Palmas Street", Suburb = "Thorn Field Estate", City = "Centurion", Province = "Gauteng"
    };

    private static OrderService Orders(SqliteTestDbFixture fixture, Guid userId, IOpenserveQualificationService qualification) =>
        new(fixture.AppDbContext, Mock.Of<IAuditService>(), H.CurrentUser(userId), Mock.Of<INotificationService>(), Mock.Of<INetworkAccountService>(), Mock.Of<IInstallationService>(),
            Mock.Of<ICoverageCheckService>(), qualification, Options.Create(new PaymentSettings()), Options.Create(new BillingSettings()), NullLogger<OrderService>.Instance);

    private static CreateOrderRequestDto PalmasOrder(Guid packageId, Guid? premisesReference = null, decimal lat = PinLat, decimal lon = PinLon) => new()
    {
        ServicePackageId = packageId, FullName = "Thandi Mokoena", Email = "thandi@example.com", PhoneNumber = "0821234567", AddressLine1 = "2 Palmas Street",
        Suburb = "Thorn Field Estate", City = "Centurion", Province = "Gauteng", PostalCode = "0157", Latitude = lat, Longitude = lon, PropertyType = PropertyType.House,
        OpenserveServicePremisesReference = premisesReference
    };

    private static void VerifyAmidQualified(Harness h, string amid, Times times) =>
        h.Client.Verify(c => c.QualifyAsync(It.Is<OpenserveQualificationQuery>(q => q.Amid == amid && !q.ForceVerify), It.IsAny<CancellationToken>()), times);

    private static void VerifyNothingOrdered(Harness h)
    {
        Assert.Empty(h.Sent);
        h.Client.Verify(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Coverage check (unresolved) → the customer chooses <paramref name="key"/> → the choice's answer.</summary>
    private static async Task<CoverageCheckResponseDto> CheckThenChooseAsync(Harness h, string key)
    {
        var check = await h.Coverage.CheckAsync(PalmasCheck());
        Assert.True(check.IsSuccess, check.Message);
        var chosen = await h.Coverage.SelectServicePremisesAsync(Choose(check.Data!.QualificationReference!.Value, key));
        Assert.True(chosen.IsSuccess, chosen.Message);
        return chosen.Data!;
    }

    // ═══ 1–2. Integration OFF → the legacy coverage path, unchanged ═══

    [Fact]
    public async Task IntegrationDisabled_CoverageUsesTheLegacyPublicGisLookup_Unchanged_AndNeverTheAuthenticatedApi()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, H.Settings(enabled: false));
        var legacy = new CoverageCheckResponseDto
        {
            CoverageAvailable = true, StatusLabel = "Available", RawStatus = "Working", MaxSpeed = 1000m, MaxSpeedUnit = "Mbps", MatchedAddress = "8 PALMAS ST MONAVONI X 6 CENTURION",
            FriendlyTitle = "Good news!", FriendlyMessage = "Fibre is available.",
            Products = new List<CoverageProductDto> { new() { ProductCode = "OFC", ProductName = "Openserve Fibre Connect", DownstreamSpeed = "1000 Mbps", UpstreamSpeed = "500 Mbps" } }
        };
        h.Gis.Setup(g => g.CheckAsync(PinLat, PinLon, It.IsAny<CancellationToken>())).ReturnsAsync(Result<CoverageCheckResponseDto>.Success(legacy));

        var result = await h.Coverage.CheckAsync(PalmasCheck());

        Assert.True(result.IsSuccess, result.Message);
        var dto = result.Data!;
        Assert.Same(legacy, dto); // the provider's own answer, as before
        Assert.Equal(CoverageMatchSource.Openserve, dto.MatchSource);
        Assert.True(dto.CoverageAvailable);
        Assert.False(dto.AddressVerificationRequired);
        Assert.False(dto.ServicePremisesSelectionRequired);
        Assert.Empty(dto.NearbyOpenserveAddresses);
        Assert.Null(dto.QualificationReference);
        h.Gis.Verify(g => g.CheckAsync(PinLat, PinLon, It.IsAny<CancellationToken>()), Times.Once);
        h.Client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.DbContext.OpenserveIntegrationLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task IntegrationDisabled_CoverageMapIncludeRule_StillDecides_AsBefore()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, H.Settings(enabled: false));
        h.Map.Setup(x => x.TryEvaluateAsync(It.IsAny<CoverageCheckRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CoverageMapEvaluationResult { Matched = true, MatchedType = CoverageMapRuleType.Include, MatchedRuleId = Guid.NewGuid(), MatchedRuleName = "Monavoni" });

        var result = await h.Coverage.CheckAsync(PalmasCheck());

        Assert.Equal(CoverageMatchSource.CoverageMapInclude, result.Data!.MatchSource);
        Assert.Equal(new[] { h.OfcPackageId }, result.Data.AvailablePackages.Select(p => p.Id));
        h.Gis.VerifyNoOtherCalls();
        h.Client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IntegrationEnabledButApiKeyMissing_IsNotTheAuthority_LegacyCoverageApplies()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var settings = H.Settings();
        settings.ApiKey = string.Empty; // e.g. an undecryptable stored key
        var h = await HarnessAsync(fixture, settings);
        h.Gis.Setup(g => g.CheckAsync(PinLat, PinLon, It.IsAny<CancellationToken>())).ReturnsAsync(Result<CoverageCheckResponseDto>.Success(new CoverageCheckResponseDto { StatusLabel = "Unavailable" }));

        var result = await h.Coverage.CheckAsync(PalmasCheck());

        Assert.True(result.IsSuccess);
        h.Gis.Verify(g => g.CheckAsync(PinLat, PinLon, It.IsAny<CancellationToken>()), Times.Once);
        h.Client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IntegrationDisabled_ServicePremisesChoice_IsNotApplicable_AndCallsNothing()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, H.Settings(enabled: false));

        var result = await h.Coverage.SelectServicePremisesAsync(Choose(Guid.NewGuid(), "0"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.VALIDATION_ERROR, result.Code);
        h.Client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IntegrationDisabled_CreateOrder_HasNoProductQualificationDependency()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, H.Settings(enabled: false));

        var created = await Orders(fixture, h.CustomerId, h.Qualification).CreateMineAsync(PalmasOrder(h.OfcPackageId));

        Assert.True(created.IsSuccess, created.Message);
        var order = await fixture.DbContext.Orders.AsNoTracking().SingleAsync();
        Assert.Equal("2 Palmas Street", order.AddressLine1);
        Assert.Null(order.OpenserveAmId);
        Assert.Null(order.OpenserveQualificationResultId);
        Assert.Equal(OpenserveAddressResolution.NotEvaluated, order.OpenservePremisesSelection);
        h.Client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task IntegrationDisabled_InitiatePayment_PassesTheFibreGate_WithoutOpenserve()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, H.Settings(enabled: false));
        var paystack = new Mock<IPaystackIntentInitiationService>();
        paystack.Setup(p => p.InitiateAsync(It.IsAny<PaystackIntentInitiationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaystackIntentInitiationResult { Success = true, Reference = "SF-INTENT-REF", RedirectUrl = "https://checkout.paystack.com/x", Currency = "ZAR" });

        var result = await IntentService(fixture, h, paystack.Object).InitiateClientPaymentAsync(PaymentRequest(h.OfcPackageId));

        Assert.NotEqual(ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED, result.Code);
        Assert.NotEqual(ErrorCodes.UPSTREAM_UNAVAILABLE, result.Code);
        var intent = await fixture.DbContext.OrderIntents.AsNoTracking().SingleAsync(); // created after the Fibre gate
        Assert.Null(intent.OpenserveQualificationResultId);
        h.Client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static OrderIntentService IntentService(SqliteTestDbFixture fixture, Harness h, IPaystackIntentInitiationService paystack)
    {
        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetMyEligibilityAsync(It.IsAny<ServicePackageType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<CustomerOrderEligibilityDto>.Success(new CustomerOrderEligibilityDto { CanCreateOrder = true }));
        return new OrderIntentService(fixture.AppDbContext, H.CurrentUser(h.CustomerId), orders.Object, null!, null!, NullLogger<OrderIntentService>.Instance, paystack,
            Mock.Of<IPayFastIntentInitiationService>(MockBehavior.Strict), Mock.Of<IOzowIntentInitiationService>(MockBehavior.Strict), Mock.Of<IPaymentApplierService>(MockBehavior.Strict),
            Mock.Of<ICustomerPaymentMandateService>(), Mock.Of<IHostEnvironment>(), Options.Create(new PayFastSettings()), Options.Create(new PaystackSettings()),
            Options.Create(new OzowSettings()), Mock.Of<IBillingDayOptionService>(), Options.Create(new BillingSettings()), h.Qualification);
    }

    private static InitiateOrderIntentPaymentRequestDto PaymentRequest(Guid packageId, Guid? premisesReference = null) => new()
    {
        ServicePackageId = packageId, FullName = "Thandi Mokoena", Email = "thandi@example.com", PhoneNumber = "0821234567", AddressLine1 = "2 Palmas Street",
        Suburb = "Thorn Field Estate", City = "Centurion", Province = "Gauteng", PostalCode = "0157", Latitude = PinLat, Longitude = PinLon, PropertyType = PropertyType.House,
        OpenserveServicePremisesReference = premisesReference
    };

    // ═══ 3. Exact match → automatic, no customer step ═══

    [Fact]
    public async Task ExactMatch_IsSelectedAutomatically_NoCustomerStep()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        const string Palmas2 = "52782199";
        var h = await HarnessAsync(fixture,
            verify: () => F.Verify((Palmas8, "8 PALMAS ST MONAVONI X 6 CENTURION", 32.77m, -25.866217m, 28.106903m), (Palmas2, "2 PALMAS ST MONAVONI X 6 CENTURION", 41.2m, -25.8665m, 28.1071m)),
            byAmid: new Dictionary<string, Func<OpenserveApiCallResult<OpenserveQualificationOutcome>>>
            {
                [Palmas2] = () => F.Call(F.Facts(Palmas2, "2", "PALMAS", "ST", "MONAVONI X 6", "CENTURION", latitude: -25.8665m, longitude: 28.1071m))
            });

        var dto = (await h.Coverage.CheckAsync(PalmasCheck())).Data!;

        Assert.False(dto.ServicePremisesSelectionRequired);
        Assert.True(dto.CoverageAvailable);
        Assert.Equal("Automatic", dto.ServicePremises!.Selection);
        Assert.False(dto.ServicePremises.CustomerConfirmed);
        Assert.Null(dto.ServicePremisesReference); // nothing to carry to checkout
        Assert.Equal(new[] { h.OfcPackageId }, dto.AvailablePackages.Select(p => p.Id));
        VerifyAmidQualified(h, Palmas2, Times.Once());
        VerifyAmidQualified(h, Palmas8, Times.Never()); // the nearest record is never used
    }

    // ═══ 4 / 19. No exact match → the customer chooses (never "no Fibre") ═══

    [Fact]
    public async Task NoExactMatch_ListsTheServiceLocations_ForTheCustomer_AndNeverPicksTheNearest()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture);

        var result = await h.Coverage.CheckAsync(PalmasCheck());

        Assert.True(result.IsSuccess, result.Message);
        var dto = result.Data!;
        Assert.True(dto.ServicePremisesSelectionRequired);
        Assert.False(dto.CoverageAvailable);
        Assert.Equal("Choose your Openserve service location", dto.StatusLabel);
        Assert.Equal(CoverageCheckService.ChooseServiceLocationTitle, dto.FriendlyTitle);
        Assert.Equal("AddressUnresolved", dto.FibreQualificationStatus);
        Assert.Empty(dto.AvailablePackages);
        Assert.Null(dto.ServicePremises);
        Assert.Equal(new[] { "8 PALMAS ST MONAVONI X 6 CENTURION", "9 DE OVALLE BLV MONAVONI X 6 CENTURION", "10 PALMAS ST MONAVONI X 6 CENTURION" },
            dto.NearbyOpenserveAddresses.Select(n => n.Address));
        Assert.Equal(new decimal?[] { 32.77m, 33.25m, 33.42m }, dto.NearbyOpenserveAddresses.Select(n => n.DistanceMeters));
        Assert.Equal(new[] { true, false, false }, dto.NearbyOpenserveAddresses.Select(n => n.IsNearest));
        Assert.DoesNotContain("isn't available", dto.FriendlyTitle + dto.FriendlyMessage);
        H.VerifyAmidQualifications(h.Client, Times.Never());
    }

    // ═══ 7 / 8 / 9 / 10 / 22. The customer's choice ═══

    [Fact]
    public async Task CustomerChoice_IsQualifiedByItsAmid_Persisted_Audited_AndNothingIsOrdered()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture);

        var dto = await CheckThenChooseAsync(h, "0");

        Assert.True(dto.CoverageAvailable);
        Assert.Equal(new[] { h.OfcPackageId }, dto.AvailablePackages.Select(p => p.Id));
        Assert.Equal("Confirmed available at your selected Openserve service location", dto.AvailablePackages[0].MatchReason);
        Assert.StartsWith("Good news — Fibre is available at the Openserve service location you selected.", dto.FriendlyTitle);
        Assert.Equal("Customer", dto.ServicePremises!.Selection);
        Assert.True(dto.ServicePremises.CustomerConfirmed);
        Assert.Equal("8 PALMAS ST MONAVONI X 6 CENTURION", dto.ServicePremises.Address);
        Assert.Equal(32.77m, dto.ServicePremises.DistanceMeters);
        Assert.Equal(dto.QualificationReference, dto.ServicePremisesReference);
        Assert.Contains("2 Palmas Street", dto.MatchedAddress); // the installation address stays the customer's
        Assert.True(dto.NearbyOpenserveAddresses[0].IsSelected);
        Assert.False(dto.ServicePremisesSelectionRequired);
        VerifyAmidQualified(h, Palmas8, Times.Once());

        var evidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync(r => r.Id == dto.ServicePremisesReference);
        Assert.Equal(OpenserveAddressResolution.CustomerSelected, evidence.AddressResolution);
        Assert.Equal(OpenserveQualificationPurpose.CustomerPremisesSelection, evidence.Purpose);
        Assert.Equal(Palmas8, evidence.Amid);
        Assert.Equal(h.CustomerId, evidence.AddressResolvedByUserId);
        Assert.NotNull(evidence.AddressResolvedAtUtc);
        Assert.Contains("Customer confirmed", evidence.AddressResolutionNote);
        Assert.Equal(3, evidence.AddressCandidateCount); // the original candidate evidence is kept
        Assert.Null(evidence.OrderId);
        Assert.StartsWith("2 Palmas Street", evidence.CustomerAddress);

        var audit = await fixture.DbContext.AuditLogs.AsNoTracking().SingleAsync(a => a.ActionType == AuditActionType.OpenserveServicePremisesSelected);
        Assert.Equal(AuditEntityType.OpenserveQualification, audit.EntityType);
        Assert.Equal(evidence.Id, audit.EntityId);
        Assert.Equal(h.CustomerId, audit.ActorUserId);
        Assert.Contains("\"selectionSource\":\"Customer\"", audit.MetadataJson);
        Assert.Contains("\"customerConfirmed\":true", audit.MetadataJson);
        Assert.Contains($"\"selectedAmid\":\"{Palmas8}\"", audit.MetadataJson);
        VerifyNothingOrdered(h);

        // No AMIDs or credentials reach the customer.
        var json = JsonSerializer.Serialize(dto);
        foreach (var leak in new[] { Palmas8, Ovalle9, Palmas10, "api_key", H.FakeApiKey, "REDACTED", "NORTH EASTERN" })
            Assert.DoesNotContain(leak, json);
        Assert.DoesNotContain(H.FakeApiKey, evidence.AddressCandidatesJson);
        Assert.DoesNotContain("api_key", audit.MetadataJson);
    }

    [Fact]
    public async Task CustomerChoice_OnlyARecordOpenserveListedForThatCheck_CanBeChosen()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture);
        var reference = (await h.Coverage.CheckAsync(PalmasCheck())).Data!.QualificationReference!.Value;

        foreach (var refused in new[]
        {
            Choose(reference, "3"),                 // not a listed record
            Choose(reference, "-1"),
            Choose(reference, "52782141"),           // an AMID is not a key
            Choose(reference, "0", confirmed: false), // confirmation is required
            Choose(Guid.NewGuid(), "0")              // not a coverage check
        })
        {
            var result = await h.Coverage.SelectServicePremisesAsync(refused);
            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.VALIDATION_ERROR, result.Code);
        }

        // An address check that already belongs to an order (e.g. SF-20261004-66E5A8CE) can't be chosen from by a customer.
        var check = await fixture.AppDbContext.OpenserveQualificationResults.SingleAsync(r => r.Id == reference);
        check.OrderId = Guid.NewGuid();
        await fixture.AppDbContext.SaveChangesAsync();
        Assert.False((await h.Coverage.SelectServicePremisesAsync(Choose(reference, "0"))).IsSuccess);

        // An expired address check must be repeated.
        check.OrderId = null;
        check.AddressVerifiedAtUtc = DateTime.UtcNow.AddMinutes(-61);
        await fixture.AppDbContext.SaveChangesAsync();
        var expired = await h.Coverage.SelectServicePremisesAsync(Choose(reference, "0"));
        Assert.Contains("expired", expired.Message);

        H.VerifyAmidQualifications(h.Client, Times.Never());
        Assert.Empty(await fixture.DbContext.AuditLogs.AsNoTracking().Where(a => a.ActionType == AuditActionType.OpenserveServicePremisesSelected).ToListAsync());
    }

    [Fact]
    public async Task CustomerChoice_IsRefused_WhenTheAddressWasMatchedAutomatically()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        const string Palmas2 = "52782199";
        var h = await HarnessAsync(fixture,
            verify: () => F.Verify((Palmas8, "8 PALMAS ST MONAVONI X 6 CENTURION", 32.77m, -25.866217m, 28.106903m), (Palmas2, "2 PALMAS ST MONAVONI X 6 CENTURION", 41.2m, -25.8665m, 28.1071m)),
            byAmid: new Dictionary<string, Func<OpenserveApiCallResult<OpenserveQualificationOutcome>>>
            {
                [Palmas2] = () => F.Call(F.Facts(Palmas2, "2", "PALMAS", "ST", "MONAVONI X 6", "CENTURION")),
                [Palmas8] = Palmas8WithFibre
            });
        var reference = (await h.Coverage.CheckAsync(PalmasCheck())).Data!.QualificationReference!.Value;

        var result = await h.Coverage.SelectServicePremisesAsync(Choose(reference, "0"));

        Assert.False(result.IsSuccess);
        Assert.Contains("matched", result.Message);
        VerifyAmidQualified(h, Palmas8, Times.Never());
    }

    // ═══ 6 / 14 / 15 / 16. Installation address vs Openserve service premises ═══

    [Fact]
    public async Task CustomerChoice_NeverOverwritesTheInstallationAddress_TheOrderKeepsBoth_AndAdminSeesWhoChose()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture);
        var chosen = await CheckThenChooseAsync(h, "0");

        var created = await Orders(fixture, h.CustomerId, h.Qualification).CreateMineAsync(PalmasOrder(h.OfcPackageId, chosen.ServicePremisesReference));

        Assert.True(created.IsSuccess, created.Message);
        var order = await fixture.DbContext.Orders.AsNoTracking().SingleAsync();
        // INSTALLATION ADDRESS — exactly what the customer entered.
        Assert.Equal("2 Palmas Street", order.AddressLine1);
        Assert.Equal("Thorn Field Estate", order.Suburb);
        Assert.Equal("Centurion", order.City);
        // OPENSERVE SERVICE PREMISES — the confirmed choice.
        Assert.Equal(Palmas8, order.OpenserveAmId);
        Assert.Equal("8 PALMAS ST MONAVONI X 6 CENTURION", order.OpenservePremisesAddress);
        Assert.Equal(OpenserveAddressResolution.CustomerSelected, order.OpenservePremisesSelection);
        Assert.Equal(h.CustomerId, order.OpenservePremisesSelectedByUserId);
        Assert.NotNull(order.OpenservePremisesCustomerConfirmedAtUtc);
        Assert.Equal(order.OpenservePremisesSelectedAtUtc, order.OpenservePremisesCustomerConfirmedAtUtc);
        Assert.Equal(32.77m, order.OpenservePremisesDistanceMeters);
        var evidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync(r => r.Id == order.OpenserveQualificationResultId);
        Assert.Equal(OpenserveAddressResolution.CustomerSelected, evidence.AddressResolution);
        Assert.Equal(order.Id, evidence.OrderId);
        VerifyNothingOrdered(h); // creating the order sends nothing either

        // Admin Order Detail: both addresses and who chose the premises — permanently.
        var fulfilment = H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, h.Client, h.Qualification), h.Qualification);
        var view = (await fulfilment.GetAsync(order.Id)).Data!;
        Assert.StartsWith("2 Palmas Street, Thorn Field Estate, Centurion", view.InstallationAddress);
        var premises = view.ServicePremises;
        Assert.True(premises.Established);
        Assert.Equal("8 PALMAS ST MONAVONI X 6 CENTURION", premises.Address);
        Assert.Equal(Palmas8, premises.Amid);
        Assert.Equal("CustomerSelected", premises.Selection);
        Assert.StartsWith("Selected by the customer (confirmed)", premises.SelectionLabel);
        Assert.Equal("Thandi Mokoena", premises.SelectedBy);
        Assert.True(premises.CustomerConfirmed);
        Assert.True(premises.DiffersFromInstallationAddress);
        Assert.Equal(32.77m, premises.DistanceMeters);
        Assert.Equal("Orderable", view.Qualification.Status);
        Assert.Equal("CustomerSelected", view.Qualification.AddressResolution);
        Assert.Contains(view.Activity, a => a.Title.StartsWith("Openserve service location chosen by the customer — 8 PALMAS ST", StringComparison.Ordinal));
        Assert.True(view.Qualification.AddressCandidates.Single(c => c.Amid == Palmas8).IsSelected);
    }

    [Fact]
    public async Task ProductOrder_IsSentOnlyOnSubmission_ForTheChosenAmid_WithInstallationAddressUnchanged()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture);
        var chosen = await CheckThenChooseAsync(h, "0");
        await Orders(fixture, h.CustomerId, h.Qualification).CreateMineAsync(PalmasOrder(h.OfcPackageId, chosen.ServicePremisesReference));
        var tracked = await fixture.AppDbContext.Orders.SingleAsync();
        tracked.Status = OrderStatus.PaymentReceived;
        var account = TestEntityFactory.CreateNetworkAccount(fixture.AppDbContext, tracked, status: NetworkAccountStatus.Pending);
        await fixture.AppDbContext.SaveChangesAsync();
        VerifyNothingOrdered(h);

        await H.Submission(fixture.AppDbContext, h.Client, h.Qualification).TrySubmitForOrderAsync(tracked.Id, account.Id);

        var command = Assert.Single(h.Sent);
        Assert.Equal(Palmas8, command.Amid);
        Assert.Equal("8 PALMAS ST", command.Street1); // Openserve's place for that AMID
        Assert.Equal("2 Palmas Street", (await fixture.DbContext.Orders.AsNoTracking().SingleAsync()).AddressLine1);
    }

    [Fact]
    public async Task Admin_CanStillOverrideACustomersChoice_ExplicitlyAndAudited_WithoutSending()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture);
        var chosen = await CheckThenChooseAsync(h, "0");
        await Orders(fixture, h.CustomerId, h.Qualification).CreateMineAsync(PalmasOrder(h.OfcPackageId, chosen.ServicePremisesReference));
        var order = await fixture.DbContext.Orders.AsNoTracking().SingleAsync();
        var adminId = TestEntityFactory.CreateUser(fixture.AppDbContext, $"admin-{Guid.NewGuid():N}@example.com", "Sipho", "Admin").Id;
        await fixture.AppDbContext.SaveChangesAsync();
        var adminQualification = H.Qualification(fixture.AppDbContext, h.Client, h.Settings, adminId);
        var fulfilment = H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, h.Client, adminQualification, userId: adminId), adminQualification, userId: adminId);

        var result = await fulfilment.SelectAddressCandidateAsync(order.Id, Palmas10, "Customer called back: their gate is on the 10 Palmas St side.");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(Palmas10, result.Data!.AmId);
        Assert.Equal("AdminSelected", result.Data.ServicePremises.Selection);
        Assert.Equal("Selected manually by Sipho Admin", result.Data.ServicePremises.SelectionLabel);
        Assert.False(result.Data.ServicePremises.CustomerConfirmed);
        Assert.Equal("2 Palmas Street", (await fixture.DbContext.Orders.AsNoTracking().SingleAsync()).AddressLine1);
        VerifyNothingOrdered(h);
    }

    [Fact]
    public async Task AdminReRun_KeepsTheCustomersChoice_WhileOpenserveStillListsIt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture);
        var chosen = await CheckThenChooseAsync(h, "0");
        await Orders(fixture, h.CustomerId, h.Qualification).CreateMineAsync(PalmasOrder(h.OfcPackageId, chosen.ServicePremisesReference));
        var order = await fixture.DbContext.Orders.AsNoTracking().SingleAsync();
        var fulfilment = H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, h.Client, h.Qualification), h.Qualification);

        var rerun = await fulfilment.RunQualificationAsync(order.Id);

        Assert.True(rerun.IsSuccess, rerun.Message);
        Assert.Equal(Palmas8, rerun.Data!.AmId);
        Assert.Equal("CustomerSelected", rerun.Data.ServicePremises.Selection);
        Assert.True(rerun.Data.ServicePremises.CustomerConfirmed);
        VerifyNothingOrdered(h);
    }

    // ═══ 11 / 12. A chosen location without Fibre: say exactly that, let them choose again ═══

    [Fact]
    public async Task ChosenLocationWithoutFibre_IsNotOrderable_SaysSoPrecisely_AndTheCustomerCanChooseAnother()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture, byAmid: new Dictionary<string, Func<OpenserveApiCallResult<OpenserveQualificationOutcome>>>
        {
            [Palmas8] = Palmas8AsStaging, [Palmas10] = Palmas10WithFibre
        });

        var first = await CheckThenChooseAsync(h, "0");

        Assert.False(first.CoverageAvailable);
        Assert.Empty(first.AvailablePackages);
        Assert.Equal(OpenserveEligibilityAssessment.SelectedPremisesUnsupportedTitle, first.FriendlyTitle);
        Assert.DoesNotContain("your address", first.FriendlyTitle + first.FriendlyMessage); // only the chosen location is described
        Assert.Equal("FtthUnavailable", first.FibreQualificationStatus);
        Assert.True(first.ServicePremisesSelectionRequired); // choose another
        Assert.True(first.NearbyOpenserveAddresses[0].IsSelected);

        var refused = await Orders(fixture, h.CustomerId, h.Qualification).CreateMineAsync(PalmasOrder(h.OfcPackageId, first.ServicePremisesReference));
        Assert.Equal(ErrorCodes.OPENSERVE_FTTH_UNAVAILABLE, refused.Code);
        Assert.StartsWith(OpenserveEligibilityAssessment.SelectedPremisesUnsupportedTitle, refused.Message);
        Assert.Empty(await fixture.DbContext.Orders.AsNoTracking().ToListAsync());

        // Back to the list: choose 10 PALMAS from the same check.
        var second = await h.Coverage.SelectServicePremisesAsync(Choose(first.QualificationReference!.Value, "2"));
        Assert.True(second.IsSuccess, second.Message);
        Assert.True(second.Data!.CoverageAvailable);
        Assert.Equal("10 PALMAS ST MONAVONI X 6 CENTURION", second.Data.ServicePremises!.Address);
        Assert.True(second.Data.NearbyOpenserveAddresses[2].IsSelected);
        Assert.False(second.Data.NearbyOpenserveAddresses[0].IsSelected);

        var created = await Orders(fixture, h.CustomerId, h.Qualification).CreateMineAsync(PalmasOrder(h.OfcPackageId, second.Data.ServicePremisesReference));
        Assert.True(created.IsSuccess, created.Message);
        Assert.Equal(Palmas10, (await fixture.DbContext.Orders.AsNoTracking().SingleAsync()).OpenserveAmId);
        VerifyNothingOrdered(h);
    }

    // ═══ Checkout re-validates the choice ═══

    [Fact]
    public async Task Checkout_WithoutAChoice_StaysUnresolved_AndAnExpiredOrForeignChoice_IsNotApplied()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture);
        var orders = Orders(fixture, h.CustomerId, h.Qualification);

        var none = await orders.CreateMineAsync(PalmasOrder(h.OfcPackageId));
        Assert.Equal(ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED, none.Code);

        var chosen = await CheckThenChooseAsync(h, "0");

        // The same choice for a different pin is not applied.
        var elsewhere = await orders.CreateMineAsync(PalmasOrder(h.OfcPackageId, chosen.ServicePremisesReference, lat: -25.870000m, lon: 28.110000m));
        Assert.Equal(ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED, elsewhere.Code);
        Assert.Equal(OpenserveQualificationService.ServicePremisesChoiceExpiredMessage, elsewhere.Message);

        // An expired choice must be made again.
        var choice = await fixture.AppDbContext.OpenserveQualificationResults.SingleAsync(r => r.Id == chosen.ServicePremisesReference);
        choice.AddressResolvedAtUtc = DateTime.UtcNow.AddMinutes(-61);
        await fixture.AppDbContext.SaveChangesAsync();
        var expired = await orders.CreateMineAsync(PalmasOrder(h.OfcPackageId, chosen.ServicePremisesReference));
        Assert.Equal(ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED, expired.Code);
        Assert.Equal(OpenserveQualificationService.ServicePremisesChoiceExpiredMessage, expired.Message);

        // A reference that is not a customer's choice (here: the coverage check itself) is not one.
        var notAChoice = await orders.CreateMineAsync(PalmasOrder(h.OfcPackageId, chosen.QualificationReference == chosen.ServicePremisesReference ? Guid.NewGuid() : chosen.QualificationReference));
        Assert.Equal(ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED, notAChoice.Code);
        Assert.Empty(await fixture.DbContext.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Checkout_ChoiceNoLongerListedByOpenserve_IsNotApplied()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var listed = true;
        var settings = H.Settings();
        settings.Qualification.ReuseMinutes = 0; // checkout asks Openserve for the candidates again
        var h = await HarnessAsync(fixture, settings, verify: () => listed
            ? UatCandidates()
            : F.Verify((Ovalle9, "9 DE OVALLE BLV MONAVONI X 6 CENTURION", 33.25m, -25.866390m, 28.107060m)));
        var chosen = await CheckThenChooseAsync(h, "0");
        listed = false;

        var result = await Orders(fixture, h.CustomerId, h.Qualification).CreateMineAsync(PalmasOrder(h.OfcPackageId, chosen.ServicePremisesReference));

        Assert.Equal(ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED, result.Code);
        Assert.Equal(OpenserveQualificationService.ServicePremisesChoiceExpiredMessage, result.Message);
    }

    [Fact]
    public async Task PaymentFirst_TheChoiceTravelsWithTheIntent_AndConversionUsesIt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var h = await HarnessAsync(fixture);
        var chosen = await CheckThenChooseAsync(h, "0");
        var paystack = new Mock<IPaystackIntentInitiationService>();
        paystack.Setup(p => p.InitiateAsync(It.IsAny<PaystackIntentInitiationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaystackIntentInitiationResult { Success = true, Reference = "SF-INTENT-REF", RedirectUrl = "https://checkout.paystack.com/x", Currency = "ZAR" });

        var result = await IntentService(fixture, h, paystack.Object).InitiateClientPaymentAsync(PaymentRequest(h.OfcPackageId, chosen.ServicePremisesReference));

        Assert.NotEqual(ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED, result.Code);
        var intent = await fixture.DbContext.OrderIntents.AsNoTracking().SingleAsync();
        var gateEvidence = await fixture.DbContext.OpenserveQualificationResults.AsNoTracking().SingleAsync(r => r.Id == intent.OpenserveQualificationResultId);
        Assert.Equal(OpenserveAddressResolution.CustomerSelected, gateEvidence.AddressResolution);
        Assert.Equal(Palmas8, gateEvidence.Amid);
        Assert.Equal(OpenserveQualificationPurpose.CheckoutGate, gateEvidence.Purpose);
        Assert.Equal("2 Palmas Street", intent.AddressLine1);
        VerifyNothingOrdered(h);

        // Conversion after payment applies that evidence — no new address check.
        var seeded = await H.SeedAsync(fixture);
        var order = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == seeded.Order.Id);
        order.AddressLine1 = "2 Palmas Street";
        order.Suburb = "Thorn Field Estate";
        order.City = "Centurion";
        order.Latitude = PinLat;
        order.Longitude = PinLon;
        order.OpenserveQualificationResultId = gateEvidence.Id;
        await fixture.AppDbContext.SaveChangesAsync();
        var verifyCallsBefore = h.Client.Invocations.Count(i => i.Arguments.FirstOrDefault() is OpenserveQualificationQuery { ForceVerify: true });

        var converted = await h.Qualification.QualifyAndPersistAsync(order.Id, OpenserveQualificationTrigger.PaymentConversion);

        Assert.Equal(OpenserveQualificationRunStatus.Qualified, converted.Status);
        var after = await fixture.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(Palmas8, after.OpenserveAmId);
        Assert.Equal(OpenserveAddressResolution.CustomerSelected, after.OpenservePremisesSelection);
        Assert.Equal("2 Palmas Street", after.AddressLine1);
        Assert.Equal(verifyCallsBefore, h.Client.Invocations.Count(i => i.Arguments.FirstOrDefault() is OpenserveQualificationQuery { ForceVerify: true }));
        VerifyNothingOrdered(h);
    }

    // ═══ Optional stricter policy: Admin approval of a customer's choice ═══

    [Fact]
    public async Task StricterPolicy_HoldsACustomersChoice_ForAdminApproval_BeforeCreateOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var settings = H.Settings();
        settings.Qualification.RequireAdminApprovalForCustomerSelectedPremises = true;
        var h = await HarnessAsync(fixture, settings);
        var chosen = await CheckThenChooseAsync(h, "0");
        var created = await Orders(fixture, h.CustomerId, h.Qualification).CreateMineAsync(PalmasOrder(h.OfcPackageId, chosen.ServicePremisesReference));
        Assert.True(created.IsSuccess, created.Message); // the customer can still order and pay
        var tracked = await fixture.AppDbContext.Orders.SingleAsync();
        tracked.Status = OrderStatus.PaymentReceived;
        var account = TestEntityFactory.CreateNetworkAccount(fixture.AppDbContext, tracked, status: NetworkAccountStatus.Pending);
        await fixture.AppDbContext.SaveChangesAsync();
        var submission = H.Submission(fixture.AppDbContext, h.Client, h.Qualification, settings);

        await submission.TrySubmitForOrderAsync(tracked.Id, account.Id);
        Assert.Empty(h.Sent);
        var fulfilment = H.Fulfilment(fixture.AppDbContext, submission, h.Qualification, settings);
        var held = (await fulfilment.GetAsync(tracked.Id)).Data!;
        Assert.Equal(OpenserveFulfilmentState.BlockedAddressReview, held.State);
        Assert.Contains("Admin approval is required", held.StateReason);
        Assert.True(held.Qualification.CanAcceptAddress);

        var accepted = await fulfilment.AcceptAddressAsync(tracked.Id, "Called the customer — 8 Palmas St is the Openserve record for their house.");
        Assert.True(accepted.IsSuccess, accepted.Message);
        await fulfilment.SubmitAsync(tracked.Id, confirmOutcomeUnknown: false);

        Assert.Equal(Palmas8, Assert.Single(h.Sent).Amid);
    }

    // ═══ 21. Historical orders stay readable ═══

    [Fact]
    public async Task LegacyOrder_ShowsItsNearestAddressAmid_AsNotVerified()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var seeded = await H.SeedAsync(fixture, amid: H.Amid);
        var order = await fixture.AppDbContext.Orders.SingleAsync(o => o.Id == seeded.Order.Id);
        await F.SeedEligibleEvidenceAsync(fixture.AppDbContext, order, F.Facts(H.Amid, suburb: null, town: "RANDBURG"));
        var client = H.Client(new List<OpenserveCreateOrderCommand>());
        var qualification = H.Qualification(fixture.AppDbContext, client);

        var view = (await H.Fulfilment(fixture.AppDbContext, H.Submission(fixture.AppDbContext, client, qualification), qualification).GetAsync(order.Id)).Data!;

        Assert.True(view.ServicePremises.Established);
        Assert.Equal(H.Amid, view.ServicePremises.Amid);
        Assert.Equal("NotEvaluated", view.ServicePremises.Selection);
        Assert.StartsWith("Nearest-address lookup", view.ServicePremises.SelectionLabel);
        Assert.False(view.ServicePremises.CustomerConfirmed);
        H.VerifyQualifyCalls(client, Times.Never());
    }
}

using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Security;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Admin package-mapping workflow end to end against a real database:
// mapping CRUD + enable/disable, Readiness, the submission safety gate,
// and retry after a mapping is corrected. Openserve values come only from
// the documented Appendix D catalogue — nothing here maps by guessing.
public class OpenservePackageMappingWorkflowTests
{
    private const string FakeApiKey = "fake-key-should-never-appear-9z9z";

    private static OpenserveFulfilmentSettings StagingSettings() => new()
    {
        Enabled = true, BaseUrl = "https://stapitrx.openserve.co.za", ApiKey = FakeApiKey, WsIspCode = "ws-marut", IspIdentifier = "WS MARUT",
        SenderId = "SMARTFUTURE", ReplyToAddress = "https://stapitrx.openserve.co.za/ws-marut/productordercallback",
        EventNotificationUrl = "https://api-uat.smartfuture.co.za/api/openserve/events"
    };

    private static IOpenserveRuntimeConfigProvider ConfigProvider()
    {
        var m = new Mock<IOpenserveRuntimeConfigProvider>();
        m.Setup(x => x.Current).Returns(StagingSettings());
        return m.Object;
    }

    private static PackageOpenserveMappingService MappingService(SqliteTestDbFixture fixture)
        => new(fixture.AppDbContext, NullLogger<PackageOpenserveMappingService>.Instance);

    private static OpenserveIntegrationAdminService AdminService(SqliteTestDbFixture fixture, Mock<IOpenserveApiClient>? client = null)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("UAT");
        return new OpenserveIntegrationAdminService(fixture.AppDbContext, (client ?? new Mock<IOpenserveApiClient>()).Object, ConfigProvider(), Mock.Of<IOpenserveSecretProtector>(),
            MappingService(fixture), environment.Object, Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            new DataProtectionKeyRingStatus { IsPersistent = true, Description = "test" }, NullLogger<OpenserveIntegrationAdminService>.Instance);
    }

    private static OpenserveOrderSubmissionService SubmissionService(SqliteTestDbFixture fixture, Mock<IOpenserveApiClient> client)
        => new(fixture.AppDbContext, client.Object, new DefaultOpenserveSubscriberReferenceGenerator(), ConfigProvider(), Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<OpenserveOrderSubmissionService>.Instance);

    private static async Task<ServicePackage> PackageAsync(SqliteTestDbFixture fixture, string name, ServicePackageType type = ServicePackageType.Fibre, ServicePackageStatus status = ServicePackageStatus.Active)
    {
        var package = new ServicePackage
        {
            Id = Guid.NewGuid(), Type = type, Status = status, Name = name, Price = 499m, SpeedLabel = "50/50 Mbps", DownloadSpeedMbps = 50, UploadSpeedMbps = 50,
            BillingCycle = ServicePackageBillingCycle.Monthly, CreatedAtUtc = DateTime.UtcNow
        };
        fixture.DbContext.ServicePackages.Add(package);
        await fixture.DbContext.SaveChangesAsync();
        return package;
    }

    private static CreatePackageOpenserveMappingRequestDto Ofc(Guid packageId, string capacity = "50", string uom = "Mbps", bool enabled = true) => new()
    {
        ServicePackageId = packageId, OpenserveProductName = "Openserve Fibre Connect", Sku = "OFC", Capacity = capacity, CapacityUom = uom, IsEnabled = enabled
    };

    private const string MappingCheckName = "All active Fibre packages have an enabled package mapping";

    private static async Task<OpenserveReadinessCheckItemDto> MappingReadinessAsync(SqliteTestDbFixture fixture)
        => (await AdminService(fixture).RunReadinessCheckAsync()).Data!.Checks.Single(c => c.Name == MappingCheckName);

    // ─── 9 / 10. Readiness tracks active Fibre packages ───────────────

    [Fact]
    public async Task ActiveFibrePackageWithoutMapping_FailsReadiness_AndMappingItClearsIt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre 50");

        var before = await MappingReadinessAsync(fixture);
        Assert.False(before.Passed);
        Assert.Contains("SmartFuture Fibre 50", before.Detail);
        Assert.Equal(1, (await AdminService(fixture).GetOverviewAsync()).Data!.UnmappedActiveFibrePackages);

        Assert.True((await MappingService(fixture).CreateAsync(Ofc(package.Id))).IsSuccess);

        Assert.True((await MappingReadinessAsync(fixture)).Passed);
        Assert.Empty((await MappingService(fixture).ListUnmappedFibrePackagesAsync()).Data!);
        Assert.Equal(0, (await AdminService(fixture).GetOverviewAsync()).Data!.UnmappedActiveFibrePackages);
    }

    // ─── 11. Disabled mapping is still unavailable ────────────────────

    [Fact]
    public async Task DisabledMapping_StillCountsAsUnavailable()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre 50");
        await MappingService(fixture).CreateAsync(Ofc(package.Id, enabled: false));

        var unmapped = (await MappingService(fixture).ListUnmappedFibrePackagesAsync()).Data!;
        Assert.Equal(PackageOpenserveMappingStatus.Disabled, Assert.Single(unmapped).MappingStatus);
        var check = await MappingReadinessAsync(fixture);
        Assert.False(check.Passed);
        Assert.Contains("mapping disabled", check.Detail);
    }

    // ─── 12 / 13. What is NOT required ────────────────────────────────

    [Fact]
    public async Task DraftInactiveArchivedFibre_AndNonFibrePackages_NeverBlockReadiness()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        await PackageAsync(fixture, "Fibre draft", status: ServicePackageStatus.Draft);
        await PackageAsync(fixture, "Fibre inactive", status: ServicePackageStatus.Inactive);
        await PackageAsync(fixture, "Fibre archived", status: ServicePackageStatus.Archived);
        foreach (var type in new[] { ServicePackageType.Security, ServicePackageType.Voice, ServicePackageType.LTE, ServicePackageType.Wireless, ServicePackageType.PrepaidFibre })
            await PackageAsync(fixture, $"{type} package", type);

        Assert.Empty((await MappingService(fixture).ListUnmappedFibrePackagesAsync()).Data!);
        Assert.True((await MappingReadinessAsync(fixture)).Passed);

        // The management table shows Draft/Inactive Fibre only when asked, never Archived or non-Fibre.
        Assert.Empty((await MappingService(fixture).ListFibrePackageMappingsAsync()).Data!);
        var all = (await MappingService(fixture).ListFibrePackageMappingsAsync(includeNonActive: true)).Data!;
        Assert.Equal(new[] { "Fibre draft", "Fibre inactive" }, all.Select(r => r.Name).OrderBy(n => n));
        Assert.All(all, r => Assert.False(r.RequiredForReadiness));
    }

    [Fact]
    public async Task NonFibrePackages_CannotBeMappedToOpenserve()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var security = await PackageAsync(fixture, "CCTV 4 IP", ServicePackageType.Security);

        var result = await MappingService(fixture).CreateAsync(Ofc(security.Id));

        Assert.False(result.IsSuccess);
        Assert.Contains("Fibre packages only", result.Message);
    }

    // ─── 14. Create / update ──────────────────────────────────────────

    [Fact]
    public async Task CreateAndUpdate_PersistTheDocumentedValues_AndNormaliseTheProductName()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre 100");
        var service = MappingService(fixture);

        var created = await service.CreateAsync(new CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id, OpenserveProductName = "openserve fibre connect", Sku = "ofc", Capacity = "100", CapacityUom = "Mbps Lite", IsEnabled = true
        });
        Assert.True(created.IsSuccess, created.Message);
        Assert.Equal("Openserve Fibre Connect", created.Data!.OpenserveProductName); // exact documented spelling
        Assert.Equal("OFC", created.Data.Sku);

        var updated = await service.UpdateAsync(created.Data.Id, new UpdatePackageOpenserveMappingRequestDto
        {
            OpenserveProductName = "Openserve Fibre Connect Premium", Sku = "OFCP", Capacity = "100", CapacityUom = "Mbps", IsEnabled = true, Notes = "Per reseller price list"
        });
        Assert.True(updated.IsSuccess, updated.Message);

        var row = Assert.Single((await service.ListFibrePackageMappingsAsync()).Data!);
        Assert.Equal(PackageOpenserveMappingStatus.Mapped, row.MappingStatus);
        Assert.Equal("OFCP", row.Mapping!.Sku);
        Assert.Equal("100", row.Mapping.Capacity);
        Assert.Equal("Mbps", row.Mapping.CapacityUom);
        Assert.Equal("Per reseller price list", row.Mapping.Notes);
        Assert.Equal(499m, row.Price);
    }

    [Theory]
    [InlineData("Openserve Fibre Connect Premium", "OFC", "50", "Mbps", "does not match SKU")]     // name/SKU mismatch
    [InlineData("Openserve Fibre Connect", "OFC", "25", "Mbps", "not a documented valid speed")]  // invented tier
    [InlineData("Openserve Fibre Connect", "OFX", "50", "Mbps", "not a SKU documented")]          // invented SKU
    public async Task UndocumentedOrInconsistentValues_AreRejected(string productName, string sku, string capacity, string uom, string expectedMessage)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre");

        var result = await MappingService(fixture).CreateAsync(new CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id, OpenserveProductName = productName, Sku = sku, Capacity = capacity, CapacityUom = uom, IsEnabled = false
        });

        Assert.False(result.IsSuccess);
        Assert.Contains(expectedMessage, result.Message);
    }

    // ─── 15. Enable / disable ─────────────────────────────────────────

    [Fact]
    public async Task EnableAndDisable_ToggleSubmissionAvailability()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre 50");
        var service = MappingService(fixture);
        var mapping = (await service.CreateAsync(Ofc(package.Id, enabled: false))).Data!;

        Assert.True((await service.SetEnabledAsync(mapping.Id, true)).Data!.IsEnabled);
        Assert.True((await MappingReadinessAsync(fixture)).Passed);

        Assert.False((await service.SetEnabledAsync(mapping.Id, false)).Data!.IsEnabled);
        Assert.False((await MappingReadinessAsync(fixture)).Passed);
    }

    [Theory]
    [InlineData("OFC", "Openserve Fibre Connect", "40", "Mbps")]
    [InlineData("OWS", "Openserve Webstream", "40", "Mbps Lite")]
    public async Task RetentionOffers_CanBeSavedDisabled_ButNeverEnabled(string sku, string productName, string capacity, string uom)
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre");
        var service = MappingService(fixture);
        var request = new CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id, OpenserveProductName = productName, Sku = sku, Capacity = capacity, CapacityUom = uom, IsEnabled = true
        };

        var enabledCreate = await service.CreateAsync(request);
        Assert.False(enabledCreate.IsSuccess);
        Assert.Contains("retention offer", enabledCreate.Message);

        request.IsEnabled = false;
        var saved = await service.CreateAsync(request);
        Assert.True(saved.IsSuccess, saved.Message);

        var enable = await service.SetEnabledAsync(saved.Data!.Id, true);
        Assert.False(enable.IsSuccess);
        Assert.Contains("retention offer", enable.Message);
    }

    // ─── 16–18. Submission gate, logging, retry ───────────────────────

    private static async Task<(Order Order, Guid NetworkAccountId)> FibreOrderAsync(SqliteTestDbFixture fixture, ServicePackage package)
    {
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"mapping-{Guid.NewGuid():N}@example.com");
        var order = TestEntityFactory.CreateOrder(db, user, package, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        order.AddressLine1 = "2 Palmas Street";
        order.City = "Krugersdorp";
        order.FullName = "Jane Doe";
        order.PhoneNumber = "0821234567";
        order.OpenserveAmId = "50782408";
        await db.SaveChangesAsync();
        var account = TestEntityFactory.CreateNetworkAccount(db, order, status: NetworkAccountStatus.Pending);
        await db.SaveChangesAsync();
        return (order, account.Id);
    }

    private static Mock<IOpenserveApiClient> CapturingClient(List<OpenserveCreateOrderCommand> sent)
    {
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .Callback<OpenserveCreateOrderCommand, CancellationToken>((c, _) => sent.Add(c))
            .ReturnsAsync(() => OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Success(Guid.NewGuid().ToString(), "POST", "https://stapitrx.openserve.co.za/ws-marut/productorder", 200,
                "{}", "{}", new OpenserveCreateOrderOutcome("1742148", "Validated", "Order received for processing. Order Id = 1742148. State = Validated")));
        return client;
    }

    [Fact]
    public async Task Submission_UsesTheConfiguredMapping()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre 100");
        await MappingService(fixture).CreateAsync(new CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id, OpenserveProductName = "Openserve Fibre Connect", Sku = "OFC", Capacity = "100", CapacityUom = "Mbps Lite", IsEnabled = true
        });
        var (order, networkAccountId) = await FibreOrderAsync(fixture, package);
        var sent = new List<OpenserveCreateOrderCommand>();

        await SubmissionService(fixture, CapturingClient(sent)).TrySubmitForOrderAsync(order.Id, networkAccountId);

        var command = Assert.Single(sent);
        Assert.Equal("Openserve Fibre Connect", command.OpenserveProductName);
        Assert.Equal("OFC", command.Sku);
        Assert.Equal("100", command.Capacity);
        Assert.Equal("Mbps Lite", command.CapacityUom);
        Assert.Equal("WS MARUT", command.IspIdentifier);
    }

    [Fact]
    public async Task MissingMapping_BlocksSubmissionSafely_AndLogsWhy_WithoutAffectingConnectivityHealth()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre 50");
        var (order, networkAccountId) = await FibreOrderAsync(fixture, package);
        var sent = new List<OpenserveCreateOrderCommand>();

        await SubmissionService(fixture, CapturingClient(sent)).TrySubmitForOrderAsync(order.Id, networkAccountId);

        Assert.Empty(sent); // nothing malformed or guessed ever leaves SmartFuture
        var openserveOrder = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == order.Id);
        Assert.Equal(OpenserveProvisioningStatus.Failed, openserveOrder.NormalizedStatus);
        Assert.Contains("No enabled Openserve package mapping", openserveOrder.LastFailureMessage);
        Assert.Contains("Package Mappings", openserveOrder.LastFailureMessage);

        var log = await fixture.DbContext.OpenserveIntegrationLogs.AsNoTracking().SingleAsync(l => l.OpenserveOrderId == openserveOrder.Id);
        Assert.False(log.IsSuccess);
        Assert.Null(log.HttpMethod);
        Assert.Null(log.Endpoint);
        Assert.StartsWith("BLOCKED (not sent to Openserve)", log.ErrorSummary);

        var overview = (await AdminService(fixture).GetOverviewAsync()).Data!;
        Assert.Null(overview.LastFailedApiCallAtUtc); // a blocked submission is not an Openserve API failure
    }

    [Fact]
    public async Task CorrectingTheMapping_AllowsRetry_WithoutRecreatingTheCustomerOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre 50");
        var (order, networkAccountId) = await FibreOrderAsync(fixture, package);
        var sent = new List<OpenserveCreateOrderCommand>();
        var submission = SubmissionService(fixture, CapturingClient(sent));

        await submission.TrySubmitForOrderAsync(order.Id, networkAccountId);
        var blocked = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == order.Id);
        Assert.Empty(sent);

        // Admin fixes the mapping, then retries the SAME Openserve order.
        await MappingService(fixture).CreateAsync(Ofc(package.Id));
        var retry = await submission.AdminRetrySubmissionAsync(blocked.Id);

        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Single(sent);
        Assert.Equal(OpenserveProvisioningStatus.Submitted.ToString(), retry.Data!.NormalizedStatus);
        Assert.Equal(blocked.Id, retry.Data.Id);
        Assert.Equal(blocked.ExternalReferenceNumber, retry.Data.ExternalReferenceNumber);
        Assert.Equal(1, await fixture.DbContext.Orders.CountAsync(o => o.Id == order.Id));
        Assert.Equal(1, await fixture.DbContext.OpenserveOrders.CountAsync(o => o.OrderId == order.Id));
    }

    [Fact]
    public async Task Retry_NeverUsesAMappingTheAdminHasDisabled()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre 50");
        var mapping = (await MappingService(fixture).CreateAsync(Ofc(package.Id))).Data!;
        var (order, networkAccountId) = await FibreOrderAsync(fixture, package);

        var failing = new Mock<IOpenserveApiClient>();
        failing.Setup(c => c.CreateOrderAsync(It.IsAny<OpenserveCreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveCreateOrderOutcome>.Failure(Guid.NewGuid().ToString(), "POST", "endpoint", 400, "{}", "{}", "GEN-45994", "No configuration for Capacity and UoM."));
        await SubmissionService(fixture, failing).TrySubmitForOrderAsync(order.Id, networkAccountId);
        var failed = await fixture.DbContext.OpenserveOrders.AsNoTracking().SingleAsync(o => o.OrderId == order.Id);
        Assert.Equal(mapping.Id, failed.PackageOpenserveMappingId);

        await MappingService(fixture).SetEnabledAsync(mapping.Id, false);
        var sent = new List<OpenserveCreateOrderCommand>();
        var retry = await SubmissionService(fixture, CapturingClient(sent)).AdminRetrySubmissionAsync(failed.Id);

        Assert.False(retry.IsSuccess);
        Assert.Empty(sent);
        Assert.Contains("No enabled Openserve package mapping", retry.Message);
    }

    // ─── 19 / 20. Access + secrecy ────────────────────────────────────

    [Fact]
    public void MappingAndIntegrationApis_AreAdminOnly()
    {
        foreach (var controller in new[] { typeof(SmartFuture.API.Controllers.PackageOpenserveMappingsController), typeof(SmartFuture.API.Controllers.OpenserveIntegrationAdminController) })
        {
            var authorize = controller.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
            Assert.NotNull(authorize);
            Assert.Equal(SmartFuture.Shared.Constants.AuthorizationPolicies.RequireAdmin, authorize!.Policy);
            Assert.Null(controller.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
            foreach (var action in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                Assert.Null(action.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        }
    }

    [Fact]
    public async Task MappingResponses_CarryNoOpenserveCredentialsOrConfiguration()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = await PackageAsync(fixture, "SmartFuture Fibre 50");
        var service = MappingService(fixture);
        var created = await service.CreateAsync(Ofc(package.Id));

        var payloads = new object?[]
        {
            created.Data, (await service.ListAsync()).Data, (await service.ListFibrePackageMappingsAsync(true)).Data,
            (await service.ListUnmappedFibrePackagesAsync()).Data, service.GetCatalogue(), (await service.SetEnabledAsync(created.Data!.Id, false)).Data
        };
        foreach (var payload in payloads)
        {
            var json = JsonSerializer.Serialize(payload);
            Assert.DoesNotContain(FakeApiKey, json);
            Assert.DoesNotContain("stapitrx", json);
            Assert.DoesNotContain("ws-marut", json);
        }

        foreach (var dto in new[] { typeof(PackageOpenserveMappingDto), typeof(FibrePackageMappingRowDto), typeof(UnmappedServicePackageDto), typeof(OpenserveCatalogueProductDto), typeof(OpenserveCatalogueSpeedDto) })
        {
            foreach (var property in dto.GetProperties())
                Assert.DoesNotMatch("(?i)apikey|secret|password|token|baseurl|replyto", property.Name);
        }
    }

    // ─── Catalogue ────────────────────────────────────────────────────

    [Fact]
    public async Task Catalogue_OffersOnlyAppendixDValues_WithRetentionOffersFlagged()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var catalogue = MappingService(fixture).GetCatalogue();

        var ofc = catalogue.Single(p => p.Sku == "OFC");
        Assert.Equal("Openserve Fibre Connect", ofc.ProductName);
        Assert.Equal(OpenserveProductCatalogue.Fibre, ofc.Technology);
        Assert.False(ofc.Speeds.Single(s => s.Capacity == "40" && s.CapacityUom == "Mbps").OrderableAsNewSalesOrder);
        Assert.True(ofc.Speeds.Single(s => s.Capacity == "100" && s.CapacityUom == "Mbps Lite").OrderableAsNewSalesOrder);

        var ows = catalogue.Single(p => p.Sku == "OWS");
        Assert.True(ows.Speeds.Single(s => s.Capacity == "40").IsRetentionOffer);
        Assert.False(ows.Speeds.Single(s => s.Capacity == "25").IsRetentionOffer);

        Assert.Equal(2, catalogue.SelectMany(p => p.Speeds).Count(s => s.IsRetentionOffer));
        Assert.Contains(catalogue, p => p.Sku == "OFTR" && !p.HasPublishedSpeedTable);
    }
}

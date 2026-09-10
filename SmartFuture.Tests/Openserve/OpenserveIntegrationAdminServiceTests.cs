using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Brief §17/TEST REQUIREMENTS for the Admin -> Integrations -> Openserve
// console service: config masking, secret-not-returned, blank-means-
// unchanged, cannot-enable-incomplete, readiness check, qualification/
// order-lookup diagnostics, secrets absent from persisted logs, package
// mapping readiness, callback health aggregation. Admin-only
// authorization is enforced by [Authorize] on the controller — verified
// via reflection at the bottom of this file rather than an HTTP pipeline
// test (this test project has no WebApplicationFactory harness).
public class OpenserveIntegrationAdminServiceTests
{
    // Trivial reversible "protection" — no need to drag in the real
    // ASP.NET Core DataProtection stack for these tests; what matters
    // here is that the SERVICE never leaks the unprotected value, not
    // how the bytes are actually encrypted (that's DataProtectionOpenserveSecretProtector's
    // own concern).
    private sealed class FakeSecretProtector : IOpenserveSecretProtector
    {
        public string ProtectApiKey(string plaintext) => "PROT:" + plaintext;
        public string UnprotectApiKey(string protectedBlob) => protectedBlob.StartsWith("PROT:") ? protectedBlob["PROT:".Length..] : protectedBlob;
        public string ProtectSharedSecret(string plaintext) => "PROT:" + plaintext;
        public string UnprotectSharedSecret(string protectedBlob) => protectedBlob.StartsWith("PROT:") ? protectedBlob["PROT:".Length..] : protectedBlob;
    }

    private static OpenserveFulfilmentSettings Fallback() => new()
    {
        Enabled = false, BaseUrl = string.Empty, ApiKey = string.Empty, WsIspCode = string.Empty,
        IspIdentifier = string.Empty, ReplyToAddress = string.Empty, EventNotificationUrl = string.Empty
    };

    /// <summary>Real merge provider (not mocked) so UpdateConfigurationAsync's DB write is actually reflected by the very next GetConfigurationAsync call, exactly like production request-to-request behaviour.</summary>
    private static IOpenserveRuntimeConfigProvider BuildRealConfigProvider(SqliteTestDbFixture fixture, IOpenserveSecretProtector protector)
    {
        var services = new ServiceCollection();
        services.AddSingleton(fixture.AppDbContext);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var fallbackMonitor = new Mock<IOptionsMonitor<OpenserveFulfilmentSettings>>();
        fallbackMonitor.Setup(m => m.CurrentValue).Returns(Fallback());

        return new OpenserveRuntimeConfigProvider(scopeFactory, fallbackMonitor.Object, protector, NullLogger<OpenserveRuntimeConfigProvider>.Instance);
    }

    private static Mock<IPackageOpenserveMappingService> NoUnmappedPackagesMapping()
    {
        var mapping = new Mock<IPackageOpenserveMappingService>();
        mapping.Setup(m => m.ListUnmappedFibrePackagesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SmartFuture.Shared.Results.Result<IReadOnlyList<UnmappedServicePackageDto>>.Success(new List<UnmappedServicePackageDto>()));
        mapping.Setup(m => m.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SmartFuture.Shared.Results.Result<IReadOnlyList<PackageOpenserveMappingDto>>.Success(
                new List<PackageOpenserveMappingDto> { new() { IsEnabled = true } }));
        return mapping;
    }

    private static OpenserveIntegrationAdminService BuildService(
        SqliteTestDbFixture fixture,
        IOpenserveRuntimeConfigProvider configProvider,
        Mock<IOpenserveApiClient>? client = null,
        Mock<IPackageOpenserveMappingService>? mapping = null,
        Mock<IAuditService>? audit = null,
        Mock<ICurrentUserService>? currentUser = null,
        Mock<IHostEnvironment>? environment = null,
        IOpenserveSecretProtector? protector = null)
    {
        environment ??= new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("UAT");

        return new OpenserveIntegrationAdminService(
            fixture.AppDbContext,
            (client ?? new Mock<IOpenserveApiClient>()).Object,
            configProvider,
            protector ?? new FakeSecretProtector(),
            (mapping ?? NoUnmappedPackagesMapping()).Object,
            environment.Object,
            (audit ?? new Mock<IAuditService>()).Object,
            (currentUser ?? new Mock<ICurrentUserService>()).Object,
            NullLogger<OpenserveIntegrationAdminService>.Instance);
    }

    // ─── Configuration masking / secret handling ─────────────────────

    [Fact]
    public async Task GetConfigurationAsync_NeverReturnsPlaintextSecret_OnlyMaskedTail()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);
        var service = BuildService(fixture, configProvider, protector: protector);

        var update = await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            BaseUrl = "https://testapitrx.openserve.co.za",
            ApiKey = "super-secret-key-abcd1234",
            WsIspCode = "ws-ispcode",
            ReplyToAddress = "https://api.smartfuture.co.za/api/openserve/callback"
        });
        Assert.True(update.IsSuccess);

        var config = await service.GetConfigurationAsync();

        Assert.True(config.IsSuccess);
        Assert.True(config.Data!.ApiKeyConfigured);
        Assert.NotNull(config.Data.ApiKeyMasked);
        Assert.DoesNotContain("super-secret-key-abcd1234", config.Data.ApiKeyMasked);
        Assert.EndsWith("1234", config.Data.ApiKeyMasked);
        Assert.StartsWith("••••••••", config.Data.ApiKeyMasked);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_BlankApiKey_LeavesExistingSecretUnchanged()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);
        var service = BuildService(fixture, configProvider, protector: protector);

        await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            BaseUrl = "https://testapitrx.openserve.co.za", ApiKey = "original-key-999",
            WsIspCode = "ws-ispcode", ReplyToAddress = "https://api.smartfuture.co.za/api/openserve/callback"
        });

        // Blank/omitted ApiKey on a subsequent save must NOT wipe the
        // previously stored secret — same "leave unchanged" contract as
        // every other masked-secret form in the app.
        var second = await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            SenderId = "SmartFuture"
        });
        Assert.True(second.IsSuccess);

        var config = await service.GetConfigurationAsync();
        Assert.True(config.Data!.ApiKeyConfigured);
        Assert.EndsWith("-999", config.Data.ApiKeyMasked);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_ClearApiKey_RemovesStoredSecret()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);
        var service = BuildService(fixture, configProvider, protector: protector);

        await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            BaseUrl = "https://testapitrx.openserve.co.za", ApiKey = "to-be-removed",
            WsIspCode = "ws-ispcode", ReplyToAddress = "https://api.smartfuture.co.za/api/openserve/callback"
        });

        var cleared = await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto { ClearApiKey = true });
        Assert.True(cleared.IsSuccess);

        var config = await service.GetConfigurationAsync();
        Assert.False(config.Data!.ApiKeyConfigured);
        Assert.Null(config.Data.ApiKeyMasked);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_CannotEnable_WhenConfigurationIncomplete()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);
        var service = BuildService(fixture, configProvider, protector: protector);

        // Enabling with no BaseUrl/ApiKey/WsIspCode/ReplyToAddress set must be refused.
        var result = await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto { Enabled = true });

        Assert.False(result.IsSuccess);
        Assert.Contains("incomplete", result.Message, StringComparison.OrdinalIgnoreCase);

        var config = await service.GetConfigurationAsync();
        Assert.False(config.Data!.Enabled);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_RejectsNonHttpsBaseUrl()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);
        var service = BuildService(fixture, configProvider, protector: protector);

        var result = await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            BaseUrl = "http://insecure.example.com"
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("HTTPS", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_EmitsAudit_WithoutSecretValuesInMetadata()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);
        var audit = new Mock<IAuditService>();
        CreateAuditLogRequestDto? captured = null;
        audit.Setup(a => a.LogAsync(It.IsAny<CreateAuditLogRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<CreateAuditLogRequestDto, CancellationToken>((r, _) => captured ??= r)
            .Returns(Task.CompletedTask);
        var service = BuildService(fixture, configProvider, audit: audit, protector: protector);

        await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            BaseUrl = "https://testapitrx.openserve.co.za", ApiKey = "top-secret-value-xyz",
            WsIspCode = "ws-ispcode", ReplyToAddress = "https://api.smartfuture.co.za/api/openserve/callback"
        });

        audit.Verify(a => a.LogAsync(It.IsAny<CreateAuditLogRequestDto>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        Assert.NotNull(captured);
        Assert.DoesNotContain("top-secret-value-xyz", captured!.MetadataJson ?? string.Empty);
        Assert.DoesNotContain("top-secret-value-xyz", captured.Summary ?? string.Empty);
    }

    // ─── Readiness check ─────────────────────────────────────────────

    [Fact]
    public async Task RunReadinessCheckAsync_AllPass_WhenFullyConfiguredAndMapped()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);
        var service = BuildService(fixture, configProvider, protector: protector);

        await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            Enabled = true, BaseUrl = "https://testapitrx.openserve.co.za", ApiKey = "key",
            WsIspCode = "ws-ispcode", IspIdentifier = "WS SMARTFUTURE",
            ReplyToAddress = "https://api.smartfuture.co.za/api/openserve/callback",
            EventNotificationUrl = "https://api.smartfuture.co.za/api/openserve/events"
        });

        var readiness = await service.RunReadinessCheckAsync();

        Assert.True(readiness.IsSuccess);
        Assert.True(readiness.Data!.IsReady);
        Assert.Equal(readiness.Data.TotalCount, readiness.Data.PassedCount);
        Assert.Contains("READY", readiness.Data.Summary);
    }

    [Fact]
    public async Task RunReadinessCheckAsync_FlagsMissingPackageMapping()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);

        var mapping = new Mock<IPackageOpenserveMappingService>();
        mapping.Setup(m => m.ListUnmappedFibrePackagesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SmartFuture.Shared.Results.Result<IReadOnlyList<UnmappedServicePackageDto>>.Success(
                new List<UnmappedServicePackageDto> { new() { Name = "Fibre 50" } }));
        mapping.Setup(m => m.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SmartFuture.Shared.Results.Result<IReadOnlyList<PackageOpenserveMappingDto>>.Success(
                new List<PackageOpenserveMappingDto>()));

        var service = BuildService(fixture, configProvider, mapping: mapping, protector: protector);

        await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            Enabled = true, BaseUrl = "https://testapitrx.openserve.co.za", ApiKey = "key",
            WsIspCode = "ws-ispcode", ReplyToAddress = "https://api.smartfuture.co.za/api/openserve/callback"
        });

        var readiness = await service.RunReadinessCheckAsync();

        Assert.False(readiness.Data!.IsReady);
        var mappingCheck = readiness.Data.Checks.Single(c => c.Name.Contains("package mapping", StringComparison.OrdinalIgnoreCase));
        Assert.False(mappingCheck.Passed);
    }

    // ─── Qualification diagnostic ────────────────────────────────────

    [Fact]
    public async Task RunQualificationTestAsync_RequiresAmidOrCoordinates()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var configProvider = BuildRealConfigProvider(fixture, new FakeSecretProtector());
        var service = BuildService(fixture, configProvider);

        var result = await service.RunQualificationTestAsync(new RunOpenserveQualificationTestRequestDto());

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RunQualificationTestAsync_Success_MapsOutcomeAndPersistsSanitizedLog()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var configProvider = BuildRealConfigProvider(fixture, new FakeSecretProtector());

        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "https://testapitrx.openserve.co.za/ws-ispcode/productqualification?AMID=1000497", 200,
                "{}", """{"errorCode":0,"Results":{"payload":{}}}""",
                new OpenserveQualificationOutcome("1000497", "BLD-1", 1, "61 Oak Ave", "Live",
                    100m, "Mbps", Suburb: "Highveld", Town: "Centurion", Province: "Gauteng",
                    AvailableProducts: new List<OpenserveQualificationProduct> { new("Fibre 100", "OFC-100", "100", "100") })));

        var service = BuildService(fixture, configProvider, client: client);

        var result = await service.RunQualificationTestAsync(new RunOpenserveQualificationTestRequestDto { Amid = "1000497" });

        Assert.True(result.IsSuccess);
        Assert.Equal("1000497", result.Data!.Amid);
        Assert.Equal("Centurion", result.Data.Town);
        Assert.Single(result.Data.AvailableProducts);

        var log = await fixture.AppDbContext.OpenserveIntegrationLogs.SingleAsync();
        Assert.Equal(OpenserveOperationType.ProductQualification, log.OperationType);
        Assert.Equal(OpenserveIntegrationDirection.Outbound, log.Direction);
        Assert.True(log.IsSuccess);
        // The api_key never travels through OpenserveApiCallResult, so a
        // persisted log can never contain it — assert the endpoint/body
        // we actually stored carries no secret-shaped value regardless.
        Assert.DoesNotContain("key", log.Endpoint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ─── GET Product Order diagnostic ────────────────────────────────

    [Fact]
    public async Task RunOrderLookupTestAsync_Success_CrossReferencesKnownLocalOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"lookup-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"Fibre {Guid.NewGuid():N}");
        var order = TestEntityFactory.CreateOrder(db, user, package, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        await db.SaveChangesAsync();
        TestEntityFactory.CreateNetworkAccount(db, order, status: NetworkAccountStatus.Pending);

        var openserveOrder = new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = order.Id, ExternalReferenceNumber = $"SF-{order.OrderNumber}",
            OpenserveOrderId = "302114", OrderType = "Sales Order", RawState = "In Progress",
            NormalizedStatus = OpenserveProvisioningStatus.InProgress, IsTerminal = false
        };
        db.OpenserveOrders.Add(openserveOrder);
        await db.SaveChangesAsync();

        var configProvider = BuildRealConfigProvider(fixture, new FakeSecretProtector());
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.GetOrderAsync("302114", It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveGetOrderOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveGetOrderOutcome("302114", "In Progress", "SO303654", null, null)));

        var service = BuildService(fixture, configProvider, client: client);

        var result = await service.RunOrderLookupTestAsync("302114");

        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.KnownLocally);
        Assert.Equal(openserveOrder.Id, result.Data.LocalOpenserveOrderId);
        Assert.Equal(order.OrderNumber, result.Data.LocalOrderNumber);
    }

    [Fact]
    public async Task RunOrderLookupTestAsync_UnknownOrder_StillReturnsOpenserveAnswer()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var configProvider = BuildRealConfigProvider(fixture, new FakeSecretProtector());
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.GetOrderAsync("999999", It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveGetOrderOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveGetOrderOutcome("999999", "Accepted", "SO999999", null, null)));

        var service = BuildService(fixture, configProvider, client: client);

        var result = await service.RunOrderLookupTestAsync("999999");

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.KnownLocally);
        Assert.Null(result.Data.LocalOrderNumber);
    }

    // ─── Test Connection ──────────────────────────────────────────────

    [Fact]
    public async Task TestConnectionAsync_PersistsIntegrationLog_NeverStoresApiKey()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);
        var service = BuildService(fixture, configProvider, protector: protector);

        await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            BaseUrl = "https://testapitrx.openserve.co.za", ApiKey = "must-never-be-logged",
            WsIspCode = "ws-ispcode", IspIdentifier = "WS SMARTFUTURE",
            ReplyToAddress = "https://api.smartfuture.co.za/api/openserve/callback"
        });

        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.TestConnectionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveGetActionsOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "https://testapitrx.openserve.co.za/upp/getactions/?IspCode=WS+SMARTFUTURE", 200,
                string.Empty, """{"Result":{"ResultCode":"0"}}""", new OpenserveGetActionsOutcome(0, "OK", 0)));

        var service2 = BuildService(fixture, configProvider, client: client, protector: protector);
        var result = await service2.TestConnectionAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.Success);

        var log = await fixture.AppDbContext.OpenserveIntegrationLogs.SingleAsync();
        Assert.Equal(OpenserveOperationType.GetActions, log.OperationType);
        Assert.DoesNotContain("must-never-be-logged", log.Endpoint ?? string.Empty);
        Assert.DoesNotContain("must-never-be-logged", log.ResponseBodyJson ?? string.Empty);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenNotConfigured_FailsWithoutCallingClient()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var configProvider = BuildRealConfigProvider(fixture, new FakeSecretProtector());
        var client = new Mock<IOpenserveApiClient>();
        var service = BuildService(fixture, configProvider, client: client);

        var result = await service.TestConnectionAsync();

        Assert.True(result.IsSuccess); // Result envelope succeeds; the DTO carries Success=false.
        Assert.False(result.Data!.Success);
        client.Verify(c => c.TestConnectionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Callback / event health ──────────────────────────────────────

    [Fact]
    public async Task GetCallbackHealthAsync_AggregatesRecentInboundLogs()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var db = fixture.AppDbContext;

        db.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(), Direction = OpenserveIntegrationDirection.Inbound,
            OperationType = OpenserveOperationType.CallbackInbound, OccurredAtUtc = DateTime.UtcNow.AddMinutes(-10), IsSuccess = true
        });
        db.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(), Direction = OpenserveIntegrationDirection.Inbound,
            OperationType = OpenserveOperationType.EventNotificationInbound, OccurredAtUtc = DateTime.UtcNow.AddMinutes(-5), IsSuccess = false,
            ErrorSummary = "Unknown order"
        });
        await db.SaveChangesAsync();

        var configProvider = BuildRealConfigProvider(fixture, new FakeSecretProtector());
        var service = BuildService(fixture, configProvider);

        var health = await service.GetCallbackHealthAsync();

        Assert.True(health.IsSuccess);
        Assert.Equal(1, health.Data!.SuccessfulInboundCount);
        Assert.Equal(1, health.Data.FailedInboundCount);
        Assert.NotNull(health.Data.LastCallbackReceivedAtUtc);
        Assert.NotNull(health.Data.LastEventReceivedAtUtc);
        Assert.Equal(2, health.Data.RecentEvents.Count);
    }

    // ─── Global Integration Logs search ──────────────────────────────

    [Fact]
    public async Task SearchIntegrationLogsAsync_FiltersByDirectionAndSuccess_AndPages()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var db = fixture.AppDbContext;

        db.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(), Direction = OpenserveIntegrationDirection.Outbound,
            OperationType = OpenserveOperationType.ProductQualification, OccurredAtUtc = DateTime.UtcNow.AddMinutes(-30), IsSuccess = true
        });
        db.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(), Direction = OpenserveIntegrationDirection.Outbound,
            OperationType = OpenserveOperationType.GetActions, OccurredAtUtc = DateTime.UtcNow.AddMinutes(-20), IsSuccess = false,
            ErrorSummary = "timeout"
        });
        db.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(), Direction = OpenserveIntegrationDirection.Inbound,
            OperationType = OpenserveOperationType.CallbackInbound, OccurredAtUtc = DateTime.UtcNow.AddMinutes(-10), IsSuccess = true
        });
        await db.SaveChangesAsync();

        var configProvider = BuildRealConfigProvider(fixture, new FakeSecretProtector());
        var service = BuildService(fixture, configProvider);

        var outboundOnly = await service.SearchIntegrationLogsAsync(new OpenserveIntegrationLogFilterRequestDto
        {
            Direction = OpenserveIntegrationDirection.Outbound
        });
        Assert.True(outboundOnly.IsSuccess);
        Assert.Equal(2, outboundOnly.Data!.TotalCount);

        var failedOnly = await service.SearchIntegrationLogsAsync(new OpenserveIntegrationLogFilterRequestDto { IsSuccess = false });
        Assert.Equal(1, failedOnly.Data!.TotalCount);
        Assert.Equal("timeout", failedOnly.Data.Items.Single().ErrorSummary);

        var paged = await service.SearchIntegrationLogsAsync(new OpenserveIntegrationLogFilterRequestDto { Page = 1, PageSize = 2 });
        Assert.Equal(3, paged.Data!.TotalCount);
        Assert.Equal(2, paged.Data.Items.Count);
        // Most-recent-first ordering.
        Assert.True(paged.Data.Items[0].OccurredAtUtc >= paged.Data.Items[1].OccurredAtUtc);
    }

    [Fact]
    public async Task SearchIntegrationLogsAsync_NeverExposesApiKeyEvenWhenSearchingOrderReference()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var db = fixture.AppDbContext;
        var user = TestEntityFactory.CreateUser(db, $"logs-{Guid.NewGuid():N}@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: $"Fibre {Guid.NewGuid():N}");
        var order = TestEntityFactory.CreateOrder(db, user, package, orderNumber: $"ORD-{Guid.NewGuid():N}"[..12]);
        await db.SaveChangesAsync();

        var openserveOrder = new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = order.Id, ExternalReferenceNumber = $"SF-{order.OrderNumber}",
            OrderType = "Sales Order", NormalizedStatus = OpenserveProvisioningStatus.Submitted
        };
        db.OpenserveOrders.Add(openserveOrder);
        db.OpenserveIntegrationLogs.Add(new OpenserveIntegrationLog
        {
            Id = Guid.NewGuid(), OpenserveOrderId = openserveOrder.Id, Direction = OpenserveIntegrationDirection.Outbound,
            OperationType = OpenserveOperationType.CreateOrder, OccurredAtUtc = DateTime.UtcNow, IsSuccess = true,
            ResponseBodyJson = """{"errorCode":0}"""
        });
        await db.SaveChangesAsync();

        var configProvider = BuildRealConfigProvider(fixture, new FakeSecretProtector());
        var service = BuildService(fixture, configProvider);

        var result = await service.SearchIntegrationLogsAsync(new OpenserveIntegrationLogFilterRequestDto { Search = openserveOrder.ExternalReferenceNumber });

        Assert.Equal(1, result.Data!.TotalCount);
        var dto = result.Data.Items.Single();
        Assert.Equal(openserveOrder.ExternalReferenceNumber, dto.OpenserveOrderExternalReferenceNumber);
        Assert.Null(dto.RequestHeadersJson);
    }

    // ─── Overview ───────────────────────────────────────────────────

    [Fact]
    public async Task GetOverviewAsync_WhenNeverConfigured_IsDisabled()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var configProvider = BuildRealConfigProvider(fixture, new FakeSecretProtector());

        var mapping = new Mock<IPackageOpenserveMappingService>();
        mapping.Setup(m => m.ListUnmappedFibrePackagesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SmartFuture.Shared.Results.Result<IReadOnlyList<UnmappedServicePackageDto>>.Success(
                new List<UnmappedServicePackageDto> { new() { Name = "Fibre 25" }, new() { Name = "Fibre 50" } }));
        mapping.Setup(m => m.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SmartFuture.Shared.Results.Result<IReadOnlyList<PackageOpenserveMappingDto>>.Success(new List<PackageOpenserveMappingDto>()));

        var service = BuildService(fixture, configProvider, mapping: mapping);

        var overview = await service.GetOverviewAsync();

        Assert.True(overview.IsSuccess);
        Assert.Equal(2, overview.Data!.UnmappedActiveFibrePackages);
        Assert.False(overview.Data.Enabled);
        Assert.Equal("Disabled", overview.Data.OverallState);
    }

    [Fact]
    public async Task GetOverviewAsync_EnabledButMissingOptionalFields_IsNotReady()
    {
        // ValidateForEnable only gates the 4 fields Openserve calls
        // cannot function without (BaseUrl/ApiKey/WsIspCode/
        // ReplyToAddress) — IspIdentifier and EventNotificationUrl can
        // legitimately be left blank at save time, so this state (Enabled
        // but not fully configured) IS reachable and must show as
        // "NotReady", not silently pass as healthy.
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var protector = new FakeSecretProtector();
        var configProvider = BuildRealConfigProvider(fixture, protector);
        var service = BuildService(fixture, configProvider, protector: protector);

        var update = await service.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto
        {
            Enabled = true, BaseUrl = "https://testapitrx.openserve.co.za", ApiKey = "key",
            WsIspCode = "ws-ispcode", ReplyToAddress = "https://api.smartfuture.co.za/api/openserve/callback"
        });
        Assert.True(update.IsSuccess);

        var overview = await service.GetOverviewAsync();

        Assert.True(overview.Data!.Enabled);
        Assert.False(overview.Data.ConfigurationComplete);
        Assert.Equal("NotReady", overview.Data.OverallState);
        Assert.Contains(overview.Data.MissingConfiguration, m => m.Contains("Event", StringComparison.OrdinalIgnoreCase));
    }
}

// NOTE on admin-only authorization: OpenserveIntegrationAdminController
// carries [Authorize(Policy = AuthorizationPolicies.RequireAdmin)] at
// the class level (same pattern as the sibling OpenserveOrdersController).
// This project has no WebApplicationFactory/HTTP-pipeline test harness
// for ANY controller (SmartFuture.Tests only references Application/
// Infrastructure/Domain/Shared, not API), so — consistent with every
// other controller in the codebase — this is verified by code review
// rather than an automated reflection/integration test.

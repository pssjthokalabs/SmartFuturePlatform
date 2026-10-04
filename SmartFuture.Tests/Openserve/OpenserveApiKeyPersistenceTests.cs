using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Security;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Infrastructure.Data;
using SmartFuture.Infrastructure.Openserve;
using SmartFuture.Infrastructure.Security;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// "I saved the Openserve API key, later Admin says it is not set."
//
// Root cause (reproduced below): the API key is stored as DataProtection
// ciphertext in OpenserveIntegrationConfigs and is never removed — but the
// DataProtection key ring was not persisted. On an IIS app pool without a
// user profile (the Site4Now hosts) that key ring lives in memory only, so
// the next process (idle shutdown / recycle / deploy) can no longer decrypt
// the stored key; the runtime config then treated it as unset.
//
// Each "process" here is a fresh DI container + DataProtection key ring +
// DbContext over the SAME database, exactly like the API restarting. No
// real credentials: the key below is fake.
public sealed class OpenserveApiKeyPersistenceTests : IDisposable
{
    private const string StagingApiKey = "fake-stg-api-key-4f8e2c71a9d3";

    private readonly string _keysDirectory = Path.Combine(Path.GetTempPath(), "sf-dataprotection-tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingLoggerProvider _logs = new();

    public void Dispose()
    {
        try { Directory.Delete(_keysDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ─── Harness ──────────────────────────────────────────────────────

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _owner.Messages.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
        }
    }

    private sealed class HostProcess : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required AppDbContext Db { get; init; }
        public required OpenserveRuntimeConfigProvider ConfigProvider { get; init; }
        public required OpenserveIntegrationAdminService Admin { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    /// <summary>Starts one API "process". <paramref name="persistentKeyRing"/>=false reproduces the pre-fix host: an in-memory key ring.</summary>
    private HostProcess StartProcess(SqliteTestDbFixture fixture, bool persistentKeyRing, OpenserveFulfilmentSettings? appSettings = null)
    {
        var db = fixture.CreateSiblingContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppDbContext>(db);

        DataProtectionKeyRingStatus keyRing;
        if (persistentKeyRing)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [SmartFutureDataProtection.KeysPathConfigKey] = _keysDirectory })
                .Build();
            keyRing = services.AddSmartFutureDataProtection(configuration, Path.GetTempPath());
        }
        else
        {
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            keyRing = new DataProtectionKeyRingStatus
            {
                IsPersistent = false, Description = "In-memory (pre-fix host).", Problem = "Neither user profile nor a key directory is available."
            };
        }

        var serviceProvider = services.BuildServiceProvider();
        var protector = new DataProtectionOpenserveSecretProtector(serviceProvider.GetRequiredService<IDataProtectionProvider>());
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));

        // Nothing in appsettings by default — the DB override must stand on its own.
        var fallback = new Mock<IOptionsMonitor<OpenserveFulfilmentSettings>>();
        fallback.Setup(m => m.CurrentValue).Returns(appSettings ?? new OpenserveFulfilmentSettings());

        var configProvider = new OpenserveRuntimeConfigProvider(serviceProvider.GetRequiredService<IServiceScopeFactory>(), fallback.Object, protector,
            loggerFactory.CreateLogger<OpenserveRuntimeConfigProvider>());

        var mapping = new Mock<IPackageOpenserveMappingService>();
        mapping.Setup(m => m.ListUnmappedFibrePackagesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<UnmappedServicePackageDto>>.Success(new List<UnmappedServicePackageDto>()));
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("UAT");

        var admin = new OpenserveIntegrationAdminService(db, Mock.Of<IOpenserveApiClient>(), configProvider, protector, mapping.Object, environment.Object,
            Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(), keyRing, loggerFactory.CreateLogger<OpenserveIntegrationAdminService>());

        return new HostProcess { Services = serviceProvider, Db = db, ConfigProvider = configProvider, Admin = admin };
    }

    private static UpdateOpenserveConfigurationRequestDto StagingConfig(string? apiKey = StagingApiKey) => new()
    {
        Enabled = false,
        BaseUrl = "stapitrx.openserve.co.za",
        ApiKey = apiKey,
        WsIspCode = "ws-marut",
        IspIdentifier = "WS MARUT",
        SenderId = "SMARTFUTURE",
        ReplyToAddress = "https://stapitrx.openserve.co.za/ws-marut/productordercallback"
    };

    private static async Task<string?> StoredCiphertextAsync(SqliteTestDbFixture fixture)
    {
        await using var db = fixture.CreateSiblingContext();
        var row = await db.OpenserveIntegrationConfigs.AsNoTracking().SingleOrDefaultAsync(c => c.Id == OpenserveIntegrationConfig.SingletonId);
        return row?.ApiKeyProtected;
    }

    // ─── Root cause, reproduced ───────────────────────────────────────

    [Fact]
    public async Task RootCause_InMemoryKeyRing_StoredKeyStaysInDatabaseButBecomesUndecryptableAfterRestart()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();

        await using (var before = StartProcess(fixture, persistentKeyRing: false))
        {
            var saved = await before.Admin.UpdateConfigurationAsync(StagingConfig());
            Assert.True(saved.IsSuccess, saved.Message);
            Assert.True((await before.Admin.GetConfigurationAsync()).Data!.ApiKeyConfigured); // "initially it works"
        }

        // Process restart (IIS idle timeout / recycle / deploy) → new in-memory key ring.
        await using var after = StartProcess(fixture, persistentKeyRing: false);

        Assert.False(string.IsNullOrEmpty(await StoredCiphertextAsync(fixture))); // (A) ruled out: not deleted from the DB
        var config = (await after.Admin.GetConfigurationAsync()).Data!;
        Assert.False(config.ApiKeyConfigured);                                     // what Admin showed as "not set"
        Assert.Equal("Database", config.FieldSources["ApiKey"]);                   // …while the DB still holds it
        Assert.Equal(OpenserveSecretStatus.StoredButUnreadable, config.ApiKeyStatus); // (B) now named explicitly
        Assert.Contains("cannot be decrypted", config.ApiKeyStatusMessage);
        Assert.False(config.KeyRingPersistent);
        Assert.Contains(_logs.Messages, m => m.Contains("cannot be decrypted"));

        var readiness = (await after.Admin.RunReadinessCheckAsync()).Data!;
        Assert.False(readiness.Checks.Single(c => c.Name.StartsWith("API Key")).Passed);
        Assert.False(readiness.Checks.Single(c => c.Name == "Secret encryption key ring persisted").Passed);
    }

    // ─── 1. Fresh scope / restart ─────────────────────────────────────

    [Fact]
    public async Task SavedApiKey_SurvivesProcessRestart_WithPersistedKeyRing()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();

        await using (var before = StartProcess(fixture, persistentKeyRing: true))
        {
            Assert.True((await before.Admin.UpdateConfigurationAsync(StagingConfig())).IsSuccess);
        }

        await using var after = StartProcess(fixture, persistentKeyRing: true);
        await after.ConfigProvider.RefreshAsync(); // what Program.cs does at startup

        Assert.Equal(StagingApiKey, after.ConfigProvider.Current.ApiKey);
        var config = (await after.Admin.GetConfigurationAsync()).Data!;
        Assert.True(config.ApiKeyConfigured);
        Assert.Equal(OpenserveSecretStatus.Configured, config.ApiKeyStatus);
        Assert.Equal("••••••••a9d3", config.ApiKeyMasked);
        Assert.True(config.KeyRingPersistent);
        Assert.True(Directory.EnumerateFiles(_keysDirectory, "key-*.xml").Any(), "the key ring must be on disk");
    }

    [Fact]
    public async Task SavedApiKey_SurvivesManyRestarts()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        await using (var first = StartProcess(fixture, persistentKeyRing: true))
        {
            await first.Admin.UpdateConfigurationAsync(StagingConfig());
        }

        for (var restart = 0; restart < 4; restart++)
        {
            await using var process = StartProcess(fixture, persistentKeyRing: true);
            Assert.True((await process.Admin.GetConfigurationAsync()).Data!.ApiKeyConfigured, $"restart #{restart + 1}");
        }
    }

    // ─── 2. Cache expiry / invalidation ───────────────────────────────

    [Fact]
    public async Task SavedApiKey_SurvivesCacheRefreshesAndAColdCache()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        await using var process = StartProcess(fixture, persistentKeyRing: true);
        await process.Admin.UpdateConfigurationAsync(StagingConfig());

        for (var i = 0; i < 5; i++)
        {
            await process.ConfigProvider.RefreshAsync(); // reconciliation worker ticks
            Assert.Equal(StagingApiKey, process.ConfigProvider.Current.ApiKey);
        }

        // A cold provider (nothing cached yet) serves appsettings only until it
        // refreshes — the admin views refresh first, so they never show that
        // transient state as "not set".
        await using var cold = StartProcess(fixture, persistentKeyRing: true);
        Assert.Equal(string.Empty, cold.ConfigProvider.Current.ApiKey);
        Assert.True((await cold.Admin.GetConfigurationAsync()).Data!.ApiKeyConfigured);
        Assert.True((await cold.Admin.GetOverviewAsync()).Data!.ApiKeyConfigured);
    }

    // ─── 3 & 4. Blank key / other edits leave the key alone ───────────

    [Fact]
    public async Task BlankApiKeyInput_AndEditingOtherFields_NeverTouchTheStoredKey()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        await using (var process = StartProcess(fixture, persistentKeyRing: true))
        {
            await process.Admin.UpdateConfigurationAsync(StagingConfig());
        }
        var original = await StoredCiphertextAsync(fixture);

        await using (var process = StartProcess(fixture, persistentKeyRing: true))
        {
            Assert.True((await process.Admin.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto { BaseUrl = "https://stapitrx.openserve.co.za", ApiKey = "" })).IsSuccess);
            Assert.True((await process.Admin.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto { SenderId = "SMARTFUTURE", ApiKey = null })).IsSuccess);
            Assert.True((await process.Admin.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto { WsIspCode = "ws-marut", ApiKey = "   " })).IsSuccess);
            Assert.True((await process.Admin.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto { HttpTimeoutSeconds = 45, PollingFallbackIntervalMinutes = 20 })).IsSuccess);
        }

        Assert.Equal(original, await StoredCiphertextAsync(fixture)); // byte-for-byte unchanged
        await using var later = StartProcess(fixture, persistentKeyRing: true);
        var config = (await later.Admin.GetConfigurationAsync()).Data!;
        Assert.True(config.ApiKeyConfigured);
        Assert.EndsWith("a9d3", config.ApiKeyMasked);
    }

    // ─── 5. Only an explicit Remove clears it ─────────────────────────

    [Fact]
    public async Task ExplicitRemove_IsTheOnlyThingThatClearsTheKey()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        await using var process = StartProcess(fixture, persistentKeyRing: true);
        await process.Admin.UpdateConfigurationAsync(StagingConfig());

        var removed = await process.Admin.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto { ClearApiKey = true });

        Assert.True(removed.IsSuccess);
        Assert.Null(await StoredCiphertextAsync(fixture));
        Assert.Equal(OpenserveSecretStatus.NotConfigured, removed.Data!.ApiKeyStatus);
        Assert.False(removed.Data.ApiKeyConfigured);
    }

    [Fact]
    public async Task TypingANewKeyAfterClickingRemove_SavesTheNewKey_InsteadOfWipingIt()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        await using var process = StartProcess(fixture, persistentKeyRing: true);
        await process.Admin.UpdateConfigurationAsync(StagingConfig("fake-old-key-0000"));

        var replaced = await process.Admin.UpdateConfigurationAsync(new UpdateOpenserveConfigurationRequestDto { ClearApiKey = true, ApiKey = "fake-new-key-7777" });

        Assert.True(replaced.Data!.ApiKeyConfigured);
        Assert.EndsWith("7777", replaced.Data.ApiKeyMasked);
        Assert.Equal("fake-new-key-7777", process.ConfigProvider.Current.ApiKey);
    }

    // ─── 6 & 7. Never returned, never logged ─────────────────────────

    [Fact]
    public async Task FullSecret_IsNeverReturnedOrLogged_AcrossSaveRestartAndUnreadableFlows()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var responses = new List<object?>();

        await using (var process = StartProcess(fixture, persistentKeyRing: true))
        {
            responses.Add((await process.Admin.UpdateConfigurationAsync(StagingConfig())).Data);
            responses.Add((await process.Admin.GetConfigurationAsync()).Data);
            responses.Add((await process.Admin.GetOverviewAsync()).Data);
            responses.Add((await process.Admin.RunReadinessCheckAsync()).Data);
            responses.Add((await process.Admin.RunConfigurationCheckAsync()).Data);
        }
        var ciphertext = await StoredCiphertextAsync(fixture);

        // Also exercise the failure path (decrypt error logging).
        await using (var broken = StartProcess(fixture, persistentKeyRing: false))
        {
            responses.Add((await broken.Admin.GetConfigurationAsync()).Data);
            responses.Add((await broken.Admin.RunReadinessCheckAsync()).Data);
        }

        foreach (var response in responses)
        {
            var json = JsonSerializer.Serialize(response);
            Assert.DoesNotContain(StagingApiKey, json);
            Assert.DoesNotContain(ciphertext!, json);
        }
        Assert.NotEmpty(_logs.Messages);
        foreach (var message in _logs.Messages)
        {
            Assert.DoesNotContain(StagingApiKey, message);
            Assert.DoesNotContain(ciphertext!, message);
        }
        Assert.False(ciphertext!.Contains(StagingApiKey), "stored value must be ciphertext, never plaintext");
    }

    // ─── 8. DB override wins ──────────────────────────────────────────

    [Fact]
    public async Task EffectiveConfig_PrefersTheDatabaseKeyOverAppSettings_AndNeedsNoAppSettingsKey()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var appSettings = new OpenserveFulfilmentSettings { ApiKey = "fake-appsettings-key-1111" };

        await using (var process = StartProcess(fixture, persistentKeyRing: true, appSettings))
        {
            await process.Admin.UpdateConfigurationAsync(StagingConfig());
            Assert.Equal(StagingApiKey, process.ConfigProvider.Current.ApiKey);
            Assert.Equal("Database", (await process.Admin.GetConfigurationAsync()).Data!.FieldSources["ApiKey"]);
        }

        // No appsettings key at all — the DB value alone keeps it configured across a restart.
        await using var restarted = StartProcess(fixture, persistentKeyRing: true, new OpenserveFulfilmentSettings());
        await restarted.ConfigProvider.RefreshAsync();
        Assert.Equal(StagingApiKey, restarted.ConfigProvider.Current.ApiKey);
    }

    [Fact]
    public async Task Readiness_ReportsWhetherTheKeyRingIsPersisted()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();

        await using (var healthy = StartProcess(fixture, persistentKeyRing: true))
        {
            var check = (await healthy.Admin.RunReadinessCheckAsync()).Data!.Checks.Single(c => c.Name == "Secret encryption key ring persisted");
            Assert.True(check.Passed);
        }

        await using var unhealthy = StartProcess(fixture, persistentKeyRing: false);
        var failing = (await unhealthy.Admin.RunReadinessCheckAsync()).Data!.Checks.Single(c => c.Name == "Secret encryption key ring persisted");
        Assert.False(failing.Passed);
        Assert.Contains((await unhealthy.Admin.RunConfigurationCheckAsync()).Data!.Issues, i => i.Contains("not persisted"));
    }
}

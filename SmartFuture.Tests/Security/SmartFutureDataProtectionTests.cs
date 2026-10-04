using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFuture.Application.Common.Security;
using SmartFuture.Infrastructure.Security;
using Xunit;

namespace SmartFuture.Tests.Security;

// The app-wide DataProtection registration. Every secret encrypted at rest
// (Openserve API key, payment mandate tokens) is only as durable as this
// key ring — these tests pin that it is written to disk, survives a fresh
// "process", and degrades loudly (never crashes startup) when it can't be.
public sealed class SmartFutureDataProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sf-dp-registration-tests", Guid.NewGuid().ToString("N"));

    public SmartFutureDataProtectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static (ServiceProvider Provider, DataProtectionKeyRingStatus Status) Register(string contentRoot, IDictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        var status = services.AddSmartFutureDataProtection(configuration, contentRoot);
        return (services.BuildServiceProvider(), status);
    }

    [Fact]
    public void DefaultsToAppDataUnderTheContentRoot_AndWritesKeysThere()
    {
        var (provider, status) = Register(_root);
        using var _ = provider;

        var expected = Path.GetFullPath(Path.Combine(_root, "App_Data", "DataProtection-Keys"));
        Assert.True(status.IsPersistent);
        Assert.Equal(expected, status.KeysDirectory);
        Assert.Null(status.Problem);

        provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("hello");
        Assert.NotEmpty(Directory.EnumerateFiles(expected, "key-*.xml"));
        Assert.Same(status, provider.GetRequiredService<DataProtectionKeyRingStatus>());
    }

    [Fact]
    public void DataProtectedByOneProcess_IsReadableByTheNext()
    {
        var keys = Path.Combine(_root, "keys");
        var settings = new Dictionary<string, string?> { [SmartFutureDataProtection.KeysPathConfigKey] = keys };

        string ciphertext;
        var (first, _) = Register(_root, settings);
        using (first)
        {
            ciphertext = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("Openserve.ApiKey.v1").Protect("secret-value");
        }

        var (second, _) = Register(_root, settings);
        using (second)
        {
            Assert.Equal("secret-value", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("Openserve.ApiKey.v1").Unprotect(ciphertext));
        }
    }

    [Fact]
    public void ApplicationNameIsUnchanged_SoTheProtectorPurposeChainIsStable()
        => Assert.Equal("SmartFuture.API", SmartFutureDataProtection.ApplicationName);

    [Fact]
    public void UnwritableKeysPath_DoesNotFailStartup_AndReportsTheProblem()
    {
        // A path "inside" an existing FILE can never be created as a directory.
        var blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var settings = new Dictionary<string, string?> { [SmartFutureDataProtection.KeysPathConfigKey] = Path.Combine(blocker, "keys") };

        var (provider, status) = Register(_root, settings);
        using var _ = provider;

        Assert.False(status.IsPersistent);
        Assert.Contains("Cannot write DataProtection keys", status.Problem);
        Assert.Contains(SmartFutureDataProtection.KeysPathConfigKey, status.Problem);
        // The app still has a working (non-durable) protector.
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
        Assert.Equal("still works", protector.Unprotect(protector.Protect("still works")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveKeysPath_BlankConfig_UsesAppData(string? configured)
        => Assert.Equal(Path.GetFullPath(Path.Combine(_rootStatic, "App_Data", "DataProtection-Keys")), SmartFutureDataProtection.ResolveKeysPath(configured, _rootStatic));

    [Fact]
    public void ResolveKeysPath_RelativeIsUnderContentRoot_AbsoluteIsKept()
    {
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "keys")), SmartFutureDataProtection.ResolveKeysPath("keys", _root));
        var absolute = Path.Combine(_root, "elsewhere", "dp");
        Assert.Equal(Path.GetFullPath(absolute), SmartFutureDataProtection.ResolveKeysPath(absolute, "C:\\ignored"));
    }

    [Fact]
    public void SeedFromDefaultProfileStore_CopiesExistingKeysOnce_AndNeverOverwrites()
    {
        var profileStore = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName;
        File.WriteAllText(Path.Combine(profileStore, "key-aaa.xml"), "<key id='aaa'/>");
        File.WriteAllText(Path.Combine(profileStore, "key-bbb.xml"), "<key id='bbb'/>");
        File.WriteAllText(Path.Combine(profileStore, "notes.txt"), "not a key");
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;

        Assert.Equal(2, SmartFutureDataProtection.SeedFromDefaultProfileStore(target, profileStore));
        Assert.False(File.Exists(Path.Combine(target, "notes.txt")));

        // Target already has keys → nothing copied, nothing overwritten.
        File.WriteAllText(Path.Combine(profileStore, "key-ccc.xml"), "<key id='ccc'/>");
        File.WriteAllText(Path.Combine(target, "key-aaa.xml"), "<key id='aaa' local='true'/>");
        Assert.Equal(0, SmartFutureDataProtection.SeedFromDefaultProfileStore(target, profileStore));
        Assert.Equal("<key id='aaa' local='true'/>", File.ReadAllText(Path.Combine(target, "key-aaa.xml")));

        Assert.Equal(0, SmartFutureDataProtection.SeedFromDefaultProfileStore(target, Path.Combine(_root, "missing")));
        Assert.Equal(0, SmartFutureDataProtection.SeedFromDefaultProfileStore(target, null));
    }

    [Fact]
    public void OptionalDpapiProtection_KeysEncryptedAtRest_StillReadableByTheNextProcess()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only; the option is ignored elsewhere.

        var settings = new Dictionary<string, string?>
        {
            [SmartFutureDataProtection.KeysPathConfigKey] = Path.Combine(_root, "dpapi-keys"),
            [SmartFutureDataProtection.ProtectKeysWithDpapiConfigKey] = "true"
        };

        string ciphertext;
        var (first, status) = Register(_root, settings);
        using (first)
        {
            Assert.True(status.KeysProtectedAtRest);
            ciphertext = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("v");
        }
        var keyXml = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(_root, "dpapi-keys"), "key-*.xml").First());
        Assert.Contains("encryptedSecret", keyXml);

        var (second, _) = Register(_root, settings);
        using (second)
        {
            Assert.Equal("v", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Unprotect(ciphertext));
        }
    }

    private static readonly string _rootStatic = Path.Combine(Path.GetTempPath(), "sf-dp-static-root");
}

using Microsoft.EntityFrameworkCore;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Brief §3: the Openserve Subscriber Reference Number must be unique
// "according to our own database constraints" and must not silently
// collide. NetworkAccountConfiguration enforces this with a filtered
// unique index (NULLs allowed, non-null values must be unique) — these
// tests exercise that constraint directly against a real relational
// engine (SQLite), not EF's InMemory provider, which does not enforce
// unique indexes at all.
public class NetworkAccountOpenserveSubscriberReferenceTests
{
    [Fact]
    public async Task TwoAccounts_WithSameSubscriberReferenceNumber_ViolatesUniqueConstraint()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var db = fixture.AppDbContext;

        var userA = TestEntityFactory.CreateUser(db, "sub-ref-a@example.com");
        var userB = TestEntityFactory.CreateUser(db, "sub-ref-b@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: "Fibre 50Mbps");
        var orderA = TestEntityFactory.CreateOrder(db, userA, package, orderNumber: "ORD-A");
        var orderB = TestEntityFactory.CreateOrder(db, userB, package, orderNumber: "ORD-B");
        await db.SaveChangesAsync();

        var accountA = TestEntityFactory.CreateNetworkAccount(db, orderA);
        accountA.OpenserveSubscriberReferenceNumber = "SUB-DUPLICATE";
        await db.SaveChangesAsync();

        var accountB = TestEntityFactory.CreateNetworkAccount(db, orderB);
        accountB.OpenserveSubscriberReferenceNumber = "SUB-DUPLICATE";

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task MultipleAccounts_WithNullSubscriberReferenceNumber_AreAllowed()
    {
        // Before Openserve submission, the reference hasn't been reserved
        // yet — many NetworkAccount rows with a null reference must
        // coexist without tripping the unique index (it's filtered to
        // NOT NULL values only).
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var db = fixture.AppDbContext;

        var userA = TestEntityFactory.CreateUser(db, "sub-ref-c@example.com");
        var userB = TestEntityFactory.CreateUser(db, "sub-ref-d@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: "Fibre 50Mbps");
        var orderA = TestEntityFactory.CreateOrder(db, userA, package, orderNumber: "ORD-C");
        var orderB = TestEntityFactory.CreateOrder(db, userB, package, orderNumber: "ORD-D");
        await db.SaveChangesAsync();

        // OpenserveSubscriberReferenceNumber left null on both — that's
        // the point of the test.
        TestEntityFactory.CreateNetworkAccount(db, orderA);
        TestEntityFactory.CreateNetworkAccount(db, orderB);

        var savedCount = await db.SaveChangesAsync();
        Assert.True(savedCount >= 2);
    }

    [Fact]
    public async Task SubscriberReferenceNumber_IsIndependentOf_Username()
    {
        // Guards against accidentally coupling the RADIUS/PPPoE username
        // to the Openserve subscriber reference — the brief is explicit
        // that this must be proven, not assumed. Setting a subscriber
        // reference that looks nothing like the username must be legal.
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var db = fixture.AppDbContext;

        var user = TestEntityFactory.CreateUser(db, "sub-ref-e@example.com");
        var package = TestEntityFactory.CreateServicePackage(db, type: ServicePackageType.Fibre, name: "Fibre 50Mbps");
        var order = TestEntityFactory.CreateOrder(db, user, package, orderNumber: "ORD-E");
        await db.SaveChangesAsync();

        var account = TestEntityFactory.CreateNetworkAccount(db, order);
        account.Username = "jane.doe";
        account.OpenserveSubscriberReferenceNumber = "OS-SUB-000123";
        await db.SaveChangesAsync();

        var reloaded = await db.NetworkAccounts.AsNoTracking().SingleAsync(n => n.Id == account.Id);
        Assert.Equal("jane.doe", reloaded.Username);
        Assert.Equal("OS-SUB-000123", reloaded.OpenserveSubscriberReferenceNumber);
        Assert.NotEqual(reloaded.Username, reloaded.OpenserveSubscriberReferenceNumber);
    }
}

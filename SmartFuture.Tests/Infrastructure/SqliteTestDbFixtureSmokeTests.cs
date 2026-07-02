using Microsoft.EntityFrameworkCore;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Tests.Infrastructure;

// Sanity checks for SqliteTestDbFixture — proves EnsureCreatedAsync
// builds the whole entity model on SQLite AND the primary seed graph
// (User → ServicePackage → Order → Invoice → LineItem) survives round-
// tripping via SaveChangesAsync. If EF ever adds a new SQL Server-only
// construct that SQLite can't materialise, this smoke test fails FIRST
// with a clear message instead of a cryptic mid-test error further out.
public class SqliteTestDbFixtureSmokeTests
{
    [Fact]
    public async Task CreateAsync_BuildsSchemaAndAllowsBasicInsert()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();

        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(
            fx.AppDbContext, type: ServicePackageType.Fibre, name: "Smoke Fibre", price: 599m, installationFee: 1500m);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg, status: OrderStatus.Submitted);
        await fx.DbContext.SaveChangesAsync();

        var loaded = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        loaded.PackageName.Should().Be("Smoke Fibre");
        loaded.PackagePrice.Should().Be(599m);
        loaded.PackageInstallationFee.Should().Be(1500m);
        loaded.UserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task InvoiceWithLineItems_RoundTrips()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();

        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg);
        await fx.DbContext.SaveChangesAsync();

        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order, totalAmount: 999m + 349.77m);
        TestEntityFactory.AddLineItem(fx.AppDbContext, invoice,
            SmartFuture.Shared.Enums.Billing.InvoiceLineItemType.InstallationFee, 999m, "Activation");
        TestEntityFactory.AddLineItem(fx.AppDbContext, invoice,
            SmartFuture.Shared.Enums.Billing.InvoiceLineItemType.ProRata, 349.77m, "Pro-rata");
        await fx.DbContext.SaveChangesAsync();

        var loaded = await fx.DbContext.Invoices
            .AsNoTracking()
            .Include(i => i.LineItems)
            .SingleAsync(i => i.Id == invoice.Id);
        loaded.LineItems.Should().HaveCount(2);
        loaded.LineItems.Sum(li => li.TotalAmount).Should().Be(999m + 349.77m);
    }
}

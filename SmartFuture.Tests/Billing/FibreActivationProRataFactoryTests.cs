using Microsoft.EntityFrameworkCore;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Billing.ProRata;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-2 tests for FibreActivationProRataFactory + the persistence
// wrapper on OrderService that consumes it. The factory is the pure
// money-shaping seam that AdminActivateServiceAsync uses to generate the
// FIRST pro-rata invoice for a Fibre-family service the moment admin
// marks it Active.
//
// What we lock here:
//   1. Fibre activation on day X with billing day Y produces:
//      Invoice with SINGLE ProRata line, amount = ProRataCalculator.Quote,
//      period = [X, next(Y)), due = next(Y).
//   2. Idempotency stamp (Order.FirstProRataInvoiceGeneratedAtUtc)
//      prevents a repeat activation from producing a second invoice.
//   3. Security packages are branched OUT — the factory returns null
//      because Security charges pro-rata at CHECKOUT, not activation.
//   4. Activation ON the billing day (billable days = 0) produces
//      null — no zero-amount invoice.
//   5. The Order.PreferredBillingDay drives the anchor even when the
//      package's billing day was different.
//
// The factory is stateless — tests can call it without any DB. The
// separate FibreActivationProRataFactory_Persistence tests round-trip
// the returned Invoice through SQLite to prove the persistence wrapper
// on OrderService still produces the intended graph.
public class FibreActivationProRataFactoryTests
{
    private static Order BuildFibreOrder(
        decimal packagePrice = 699m,
        int preferredBillingDay = 15,
        ServicePackageType packageType = ServicePackageType.Fibre,
        DateTime? firstProRataStamp = null)
        => new()
        {
            Id = Guid.NewGuid(),
            OrderNumber = "SF-ORD-FIBRE-1",
            UserId = Guid.NewGuid(),
            Status = OrderStatus.PaymentReceived,
            PackageName = "Fibre 100/50",
            PackageType = packageType,
            PackagePrice = packagePrice,
            PreferredBillingDay = preferredBillingDay,
            PackageBillingCycle = ServicePackageBillingCycle.Monthly,
            AddressLine1 = "1 Test St",
            FirstProRataInvoiceGeneratedAtUtc = firstProRataStamp,
        };

    // ─── Category 2 — Fibre activation happy path ───────────────────

    [Fact]
    public void FibreActivation_GeneratesProRataInvoice()
    {
        // June 8 activation, billing day 15 → [Jun 8, Jun 15) = 7 days,
        // monthly = 1499 → pro-rata = round(1499 * 7 / 30, 2, AwayFromZero) = 349.77.
        var order = BuildFibreOrder(packagePrice: 1499m, preferredBillingDay: 15);
        var activation = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 8, 10, 0, 0, DateTimeKind.Utc);
        var settings = new BillingSettings();

        var built = FibreActivationProRataFactory.TryBuild(order, activation, now, settings, actingUserId: null);

        built.Should().NotBeNull();
        built!.Invoice.TotalAmount.Should().Be(349.77m);
        built.Invoice.BalanceDue.Should().Be(349.77m);
        built.Invoice.SubtotalAmount.Should().Be(349.77m);
        built.Invoice.AmountPaid.Should().Be(0m);
        built.Invoice.CurrencyCode.Should().Be("ZAR");
        built.Invoice.Status.Should().Be(InvoiceStatus.Issued);
        built.Invoice.IssuedAtUtc.Should().Be(now);
        built.Invoice.PeriodStartUtc.Should().Be(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc));
        built.Invoice.PeriodEndUtc.Should().Be(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        built.Invoice.DueAtUtc.Should().Be(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        built.Invoice.OrderId.Should().Be(order.Id);
        built.Invoice.LineItems.Should().HaveCount(1);
        var line = built.Invoice.LineItems.Single();
        line.LineType.Should().Be(InvoiceLineItemType.ProRata);
        line.TotalAmount.Should().Be(349.77m);
        line.UnitAmount.Should().Be(349.77m);
        line.Quantity.Should().Be(1);
    }

    [Fact]
    public void FibreActivation_UsesOrderPreferredBillingDay_NotPackageDefault()
    {
        // Order was placed with a picker choice of 25 — factory must
        // anchor to 25, NOT the BillingSettings default (30).
        var order = BuildFibreOrder(packagePrice: 899m, preferredBillingDay: 25);
        var activation = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);
        var now = activation;
        var settings = new BillingSettings(); // DefaultBillingDay=30

        var built = FibreActivationProRataFactory.TryBuild(order, activation, now, settings, null);

        built.Should().NotBeNull();
        built!.Invoice.PeriodEndUtc.Should().Be(new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc));
        built.Invoice.DueAtUtc.Should().Be(new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void FibreActivation_ActivationOnBillingDay_ReturnsNull_NoZeroInvoice()
    {
        // Activate on the same day as the billing day → 0 billable days.
        var order = BuildFibreOrder(preferredBillingDay: 15);
        var activation = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        var built = FibreActivationProRataFactory.TryBuild(order, activation, activation, new BillingSettings(), null);

        // Contract: no invoice for a zero-day / zero-amount pro-rata.
        built.Should().BeNull();
    }

    // ─── Idempotency ─────────────────────────────────────────────

    [Fact]
    public void FibreActivation_IsIdempotent_WhenFirstProRataStampSet()
    {
        var order = BuildFibreOrder(
            preferredBillingDay: 15,
            firstProRataStamp: new DateTime(2026, 6, 8, 10, 0, 0, DateTimeKind.Utc));
        var activation = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);

        var built = FibreActivationProRataFactory.TryBuild(order, activation, activation, new BillingSettings(), null);

        built.Should().BeNull(
            "the idempotency stamp on Order.FirstProRataInvoiceGeneratedAtUtc must short-circuit " +
            "a repeat activation so a re-issued invoice can't happen");
    }

    // ─── Product-type branch ─────────────────────────────────────

    [Fact]
    public void SecurityPackage_ReturnsNull_ProRataAlreadyChargedAtCheckout()
    {
        // Security packages charge pro-rata at CHECKOUT, so the activation
        // path must not double-charge.
        var order = BuildFibreOrder(packageType: ServicePackageType.Security, preferredBillingDay: 15);
        var activation = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);

        var built = FibreActivationProRataFactory.TryBuild(order, activation, activation, new BillingSettings(), null);

        built.Should().BeNull();
    }

    // ─── Persistence round-trip via SQLite ───────────────────────

    [Fact]
    public async Task FibreActivation_GeneratesProRataInvoice_RoundTripsThroughSqlite()
    {
        // Prove the constructed Invoice + ProRata line item survives
        // SaveChangesAsync with the expected fields intact, including the
        // Order.FirstProRataInvoiceGeneratedAtUtc idempotency flag the
        // caller (OrderService) stamps AFTER the factory returns.
        await using var fx = await SqliteTestDbFixture.CreateAsync();

        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext,
            type: ServicePackageType.Fibre, price: 1499m, installationFee: 100m);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg,
            preferredBillingDay: 15, status: OrderStatus.Active);
        order.PackagePrice = 1499m;
        await fx.DbContext.SaveChangesAsync();

        var activation = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);
        var built = FibreActivationProRataFactory.TryBuild(order, activation, activation,
            new BillingSettings(), user.Id);
        built.Should().NotBeNull();

        // Mimic the persistence wrapper on OrderService.
        fx.DbContext.Invoices.Add(built!.Invoice);
        order.FirstProRataInvoiceGeneratedAtUtc = built.IssuedAtUtc;
        await fx.DbContext.SaveChangesAsync();

        var loaded = await fx.DbContext.Invoices
            .AsNoTracking()
            .Include(i => i.LineItems)
            .SingleAsync(i => i.OrderId == order.Id);
        loaded.TotalAmount.Should().Be(349.77m);
        loaded.LineItems.Should().HaveCount(1);
        loaded.LineItems.Single().LineType.Should().Be(InvoiceLineItemType.ProRata);

        var orderRow = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        orderRow.FirstProRataInvoiceGeneratedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task FibreActivation_IsIdempotent_DoesNotDuplicateProRataInvoice()
    {
        // Simulate two sequential activation runs — the second must
        // find the stamp and skip.
        await using var fx = await SqliteTestDbFixture.CreateAsync();

        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext,
            type: ServicePackageType.Fibre, price: 1499m);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg, preferredBillingDay: 15);
        order.PackagePrice = 1499m;
        await fx.DbContext.SaveChangesAsync();

        var activation = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);

        // First activation → invoice + stamp.
        var built1 = FibreActivationProRataFactory.TryBuild(order, activation, activation, new BillingSettings(), user.Id);
        fx.DbContext.Invoices.Add(built1!.Invoice);
        order.FirstProRataInvoiceGeneratedAtUtc = built1.IssuedAtUtc;
        await fx.DbContext.SaveChangesAsync();

        // Second activation → factory returns null, no new invoice.
        var built2 = FibreActivationProRataFactory.TryBuild(order, activation, activation, new BillingSettings(), user.Id);
        built2.Should().BeNull();

        (await fx.DbContext.Invoices.CountAsync(i => i.OrderId == order.Id)).Should().Be(1);
    }
}

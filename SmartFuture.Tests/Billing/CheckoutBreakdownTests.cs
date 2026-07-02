using Microsoft.EntityFrameworkCore;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Billing.ProRata;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-2 tests for CheckoutBreakdownCalculator — the shared helper
// OrderIntentService.InitiateClientPaymentAsync and
// OrderIntentService.ConvertIntentPaymentToPaidOrderAsync both delegate
// to. Locks the "due today" invariants for the two live product lines
// (Security = activation + pro-rata, Fibre = activation only) plus the
// variant-override rules.
//
// The persistence tests round-trip the resulting Order + Invoice via
// SQLite to prove the intent-conversion snapshot is coherent when the
// helper's numbers land in the DB.
public class CheckoutBreakdownTests
{
    // ─── Category 1 — Security checkout maths ───────────────────────

    [Fact]
    public void SecurityCheckout_WithBasePackage_ChargesActivationPlusProRata()
    {
        // Security 4-IP: R699/mo + R999 activation, billing day 15,
        // order placed on June 4. Days [Jun 4, Jun 15) = 11 days.
        // Pro-rata = round(699 * 11 / 30, 2, AwayFromZero) = 256.30.
        var pkg = new ServicePackage
        {
            Type = ServicePackageType.Security,
            Name = "CCTV 4 IP",
            Price = 699m,
            InstallationFee = 999m,
            HasFreeInstallation = false,
        };
        var now = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);
        var settings = new BillingSettings();

        var breakdown = CheckoutBreakdownCalculator.Compute(pkg, billingDay: 15, now, settings);

        breakdown.ActivationFee.Should().Be(999m);
        breakdown.ProRataAmount.Should().Be(256.30m);
        breakdown.ProRataDays.Should().Be(11);
        breakdown.TotalDueNow.Should().Be(999m + 256.30m);
        breakdown.ProRataPeriodStartUtc.Should().Be(new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc));
        breakdown.ProRataPeriodEndUtc.Should().Be(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void SecurityCheckout_WithVariant_UsesVariantMonthlyPriceForProRata()
    {
        // Base package 699/mo. Variant "8 IP" 1499/mo, same activation fee.
        // Variant price MUST win in the pro-rata calc.
        var pkg = new ServicePackage
        {
            Type = ServicePackageType.Security,
            Name = "CCTV",
            Price = 699m,
            InstallationFee = 999m,
        };
        var variant = new ServicePackageVariant
        {
            Name = "8 IP",
            Price = 1499m,
            InstallationFee = null,        // inherit
            HasFreeInstallation = null,    // inherit
        };
        var now = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var breakdown = CheckoutBreakdownCalculator.Compute(pkg, billingDay: 15, now, new BillingSettings(), variant);

        // Pro-rata = round(1499 * 11 / 30, 2, AwayFromZero) = 549.63
        breakdown.ActivationFee.Should().Be(999m);
        breakdown.ProRataAmount.Should().Be(549.63m);
        breakdown.TotalDueNow.Should().Be(999m + 549.63m);
    }

    [Fact]
    public void SecurityCheckout_WithVariantInstallationOverride_UsesVariantActivationFee()
    {
        // Base activation 999. Variant "8 IP" overrides installation
        // fee to 1499. Variant fee must win.
        var pkg = new ServicePackage
        {
            Type = ServicePackageType.Security,
            Name = "CCTV",
            Price = 699m,
            InstallationFee = 999m,
        };
        var variant = new ServicePackageVariant
        {
            Name = "8 IP",
            Price = 1499m,
            InstallationFee = 1499m,
            HasFreeInstallation = false,
        };
        var now = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var breakdown = CheckoutBreakdownCalculator.Compute(pkg, billingDay: 15, now, new BillingSettings(), variant);

        breakdown.ActivationFee.Should().Be(1499m);
        breakdown.TotalDueNow.Should().Be(1499m + breakdown.ProRataAmount);
    }

    [Fact]
    public void SecurityCheckout_WithFreeVariantInstallation_ChargesOnlyProRata()
    {
        // Variant marks activation as FREE, overrides the package's
        // non-free flag. R100 launch floor must NOT kick in because the
        // package is genuinely free-activation.
        var pkg = new ServicePackage
        {
            Type = ServicePackageType.Security,
            Name = "CCTV",
            Price = 699m,
            InstallationFee = 999m,
            HasFreeInstallation = false,
        };
        var variant = new ServicePackageVariant
        {
            Name = "8 IP Promo",
            Price = 899m,
            InstallationFee = null,
            HasFreeInstallation = true,   // <- explicit free override
        };
        var now = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var breakdown = CheckoutBreakdownCalculator.Compute(pkg, billingDay: 15, now, new BillingSettings(), variant);

        breakdown.ActivationFee.Should().Be(0m);
        breakdown.ProRataAmount.Should().BeGreaterThan(0m);
        breakdown.TotalDueNow.Should().Be(breakdown.ProRataAmount);
    }

    [Fact]
    public void SecurityCheckout_OrderPlacedOnBillingDay_ProRataZero()
    {
        var pkg = new ServicePackage
        {
            Type = ServicePackageType.Security,
            Name = "CCTV",
            Price = 699m,
            InstallationFee = 999m,
        };
        var now = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        var breakdown = CheckoutBreakdownCalculator.Compute(pkg, billingDay: 15, now, new BillingSettings());

        breakdown.ProRataAmount.Should().Be(0m);
        breakdown.ProRataDays.Should().Be(0);
        breakdown.ProRataPeriodStartUtc.Should().BeNull();
        breakdown.ProRataPeriodEndUtc.Should().BeNull();
        breakdown.TotalDueNow.Should().Be(999m);
    }

    // ─── Category 2 — Fibre checkout maths ─────────────────────────

    [Fact]
    public void FibreCheckout_ChargesInstallationOnly()
    {
        // Fibre's monthly meter starts AFTER activation. Checkout only
        // collects the activation fee — no pro-rata line.
        var pkg = new ServicePackage
        {
            Type = ServicePackageType.Fibre,
            Name = "Fibre 100/50",
            Price = 899m,
            InstallationFee = 100m,
        };
        var now = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var breakdown = CheckoutBreakdownCalculator.Compute(pkg, billingDay: 15, now, new BillingSettings());

        breakdown.ActivationFee.Should().Be(100m);
        breakdown.ProRataAmount.Should().Be(0m);
        breakdown.ProRataDays.Should().Be(0);
        breakdown.TotalDueNow.Should().Be(100m);
    }

    [Fact]
    public void FibreCheckout_DoesNotChargeProRataBeforeActivation()
    {
        // Regardless of billing day / order date, Fibre must never
        // charge a pro-rata line at checkout.
        var pkg = new ServicePackage
        {
            Type = ServicePackageType.Fibre,
            Name = "Fibre 200",
            Price = 1299m,
            InstallationFee = 100m,
        };
        var settings = new BillingSettings();

        foreach (var day in new[] { 1, 15, 25, 30 })
        {
            var now = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);
            var breakdown = CheckoutBreakdownCalculator.Compute(pkg, day, now, settings);
            breakdown.ProRataAmount.Should().Be(0m,
                $"Fibre checkout never bills pro-rata at checkout — billingDay={day}");
        }
    }

    [Fact]
    public void FibreCheckout_FreeInstallation_CanHaveZeroDueToday()
    {
        // Free-activation Fibre package → 0 activation, 0 pro-rata,
        // 0 due today (customer only pays their monthly after admin
        // activates the service).
        var pkg = new ServicePackage
        {
            Type = ServicePackageType.Fibre,
            Name = "Free Fibre Promo",
            Price = 599m,
            InstallationFee = 0m,
            HasFreeInstallation = true,
        };
        var now = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        var breakdown = CheckoutBreakdownCalculator.Compute(pkg, billingDay: 15, now, new BillingSettings());

        breakdown.ActivationFee.Should().Be(0m);
        breakdown.ProRataAmount.Should().Be(0m);
        breakdown.TotalDueNow.Should().Be(0m);
    }

    [Fact]
    public void FibreCheckout_LaunchFloorAppliesWhenFeeMissing()
    {
        // Fibre with null / 0 configured fee and NOT marked free → the
        // R100 pre-release launch floor kicks in.
        var pkg = new ServicePackage
        {
            Type = ServicePackageType.Fibre,
            Name = "Legacy Fibre",
            Price = 899m,
            InstallationFee = null,
            HasFreeInstallation = false,
        };
        var breakdown = CheckoutBreakdownCalculator.Compute(pkg, billingDay: 15,
            new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc), new BillingSettings());
        breakdown.ActivationFee.Should().Be(CheckoutBreakdownCalculator.MinimumInstallationFee); // 100
    }

    // ─── Category 1 — Persistence round-trip (Security intent → paid) ───

    [Fact]
    public async Task SecurityPaidIntent_CreatesActivationAndProRataInvoiceLines()
    {
        // Simulate the OUTCOME of ConvertIntentPaymentToPaidOrderAsync:
        // an Order (PaymentReceived), an Invoice with two line items
        // (InstallationFee + ProRata), FirstProRataInvoiceGeneratedAtUtc
        // stamped, PeriodStartUtc/PeriodEndUtc set. Round-tripping the
        // graph through SQLite proves the persistence side is coherent
        // for the numbers CheckoutBreakdownCalculator produces.
        await using var fx = await SqliteTestDbFixture.CreateAsync();

        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext,
            type: ServicePackageType.Security, price: 699m, installationFee: 999m);
        var variant = TestEntityFactory.CreateVariant(fx.AppDbContext, pkg,
            name: "8 IP", price: 1499m);
        await fx.DbContext.SaveChangesAsync();

        var now = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);
        var breakdown = CheckoutBreakdownCalculator.Compute(pkg, billingDay: 15, now, new BillingSettings(), variant);

        // Persist the "post-conversion" graph the OrderIntentService writes.
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg, variant,
            preferredBillingDay: 15);
        order.FirstProRataInvoiceGeneratedAtUtc = now;
        await fx.DbContext.SaveChangesAsync();

        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order,
            totalAmount: breakdown.TotalDueNow,
            dueAtUtc: now.AddDays(7),
            issuedAtUtc: now,
            periodStartUtc: breakdown.ProRataPeriodStartUtc,
            periodEndUtc: breakdown.ProRataPeriodEndUtc);
        TestEntityFactory.AddLineItem(fx.AppDbContext, invoice,
            InvoiceLineItemType.InstallationFee, breakdown.ActivationFee, "Activation");
        TestEntityFactory.AddLineItem(fx.AppDbContext, invoice,
            InvoiceLineItemType.ProRata, breakdown.ProRataAmount, "Pro-rata", sortOrder: 1);
        await fx.DbContext.SaveChangesAsync();

        var loaded = await fx.DbContext.Invoices.AsNoTracking()
            .Include(i => i.LineItems)
            .SingleAsync(i => i.OrderId == order.Id);
        loaded.LineItems.Should().HaveCount(2);
        loaded.LineItems.Sum(li => li.TotalAmount).Should().Be(breakdown.TotalDueNow);
        loaded.LineItems.Should().Contain(li => li.LineType == InvoiceLineItemType.InstallationFee);
        loaded.LineItems.Should().Contain(li => li.LineType == InvoiceLineItemType.ProRata);
        loaded.PeriodStartUtc.Should().Be(new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc));
        loaded.PeriodEndUtc.Should().Be(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        var orderRow = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        orderRow.FirstProRataInvoiceGeneratedAtUtc.Should().NotBeNull();
        orderRow.PackagePrice.Should().Be(1499m, "variant price snapshots onto the Order at conversion");
        orderRow.PreferredBillingDay.Should().Be(15);
        orderRow.ServicePackageVariantId.Should().Be(variant.Id);
    }

    [Fact]
    public async Task SecurityPaidIntent_WithVariant_SnapshotsVariantIdAndPrice()
    {
        // Prove the (ServicePackageVariantId, PackagePrice, PackageVariantName)
        // snapshot survives the DB round-trip — future variant price
        // edits do NOT retroactively re-price the historical order.
        await using var fx = await SqliteTestDbFixture.CreateAsync();

        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext,
            type: ServicePackageType.Security, price: 699m, installationFee: 999m);
        var variant = TestEntityFactory.CreateVariant(fx.AppDbContext, pkg, name: "8 IP", price: 1499m);
        await fx.DbContext.SaveChangesAsync();

        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg, variant, preferredBillingDay: 15);
        await fx.DbContext.SaveChangesAsync();

        // Admin later bumps the variant price.
        variant.Price = 1799m;
        await fx.DbContext.SaveChangesAsync();

        var loaded = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        loaded.PackagePrice.Should().Be(1499m, "the order snapshotted the variant price at conversion time");
        loaded.PackageVariantName.Should().Be("8 IP");
        loaded.ServicePackageVariantId.Should().Be(variant.Id);
    }

    [Fact]
    public async Task SecurityPaidIntent_WebhookReplay_DoesNotDuplicateProRataLine()
    {
        // The Order.FirstProRataInvoiceGeneratedAtUtc idempotency stamp
        // is the guard. This test locks the "stamp already set → skip"
        // shape: OrderService.TryGenerateFibreProRataInvoiceAsync (which
        // is also what a re-processed Security webhook would land on
        // via AdminActivateServiceAsync for the Fibre-managed activation
        // step) must return null so no second invoice is written.
        await using var fx = await SqliteTestDbFixture.CreateAsync();

        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext,
            type: ServicePackageType.Security, price: 699m, installationFee: 999m);
        await fx.DbContext.SaveChangesAsync();

        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg, preferredBillingDay: 15);
        order.PackagePrice = 699m;
        order.FirstProRataInvoiceGeneratedAtUtc = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);
        await fx.DbContext.SaveChangesAsync();

        // Replay: even though the package type is Security AND checkout
        // would have paid pro-rata, the guard is the stamp — so any
        // downstream pro-rata invoice path (Fibre-activation or a
        // duplicated Security webhook) short-circuits.
        var built = FibreActivationProRataFactory.TryBuild(order,
            activationDateUtc: new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc),
            nowUtc: new DateTime(2026, 6, 8, 10, 0, 0, DateTimeKind.Utc),
            billingSettings: new BillingSettings(), actingUserId: user.Id);

        built.Should().BeNull();
        (await fx.DbContext.Invoices.CountAsync()).Should().Be(0);
    }
}

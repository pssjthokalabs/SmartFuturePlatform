using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-4 DB integration tests for ServiceBillingScheduleService.
//
// The service anchors the recurring billing cycle the first time a
// service-fee invoice is paid. Contract locks:
//
//   • Service invoice = one that carries ANY ServicePackage or ProRata
//     line item. Installation-fee-only invoices don't count.
//   • Idempotent — one schedule per order. A later invoice being paid
//     must NOT re-anchor.
//   • NetworkAccount must exist. If not (race with the applier's
//     provisioning hook), do nothing quietly — the next paid service
//     invoice will retry.
//   • Amount snapshots off Order.PackagePrice (which itself was
//     snapshotted from the variant at intent conversion). Admin later
//     bumping the package's Price does NOT re-price the schedule.
//   • AnchorDayOfMonth = Order.PreferredBillingDay.
//   • First cycle anchor = invoice.PeriodEndUtc if set, else
//     ProRataCalculator.NextBillingDate(paidAt, billingDay). Late
//     payments do NOT shift the cadence forward.
//   • Order.NextPayDateUtc is synced to the schedule's nextDue.
public class ServiceBillingScheduleServiceTests
{
    private static ServiceBillingScheduleService Build(SqliteTestDbFixture fx, int daysBeforeDue = 5) =>
        new(fx.AppDbContext,
            Options.Create(new AutoBillingSettings { GenerateInvoicesDaysBeforeDue = daysBeforeDue }),
            NullLogger<ServiceBillingScheduleService>.Instance);

    // Seed the graph:
    // User → ServicePackage → Order → NetworkAccount → Invoice with line items.
    // Caller picks which line items to add.
    private static async Task<(SqliteTestDbFixture fx,
        SmartFuture.Domain.Identity.User user,
        SmartFuture.Domain.Orders.Order order,
        SmartFuture.Domain.NetworkAccounts.NetworkAccount networkAccount,
        SmartFuture.Domain.Billing.Invoice invoice)>
        SeedPaidInvoiceAsync(
            ServicePackageType packageType = ServicePackageType.Fibre,
            decimal packagePrice = 699m,
            int preferredBillingDay = 15,
            OrderStatus orderStatus = OrderStatus.PaymentReceived,
            DateTime? invoicePeriodEndUtc = null,
            bool addServicePackageLine = false,
            bool addProRataLine = true,
            bool addInstallationLine = false,
            NetworkAccountStatus? naStatus = NetworkAccountStatus.Active)
    {
        var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext,
            type: packageType, price: packagePrice);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg,
            preferredBillingDay: preferredBillingDay, status: orderStatus);
        order.PackagePrice = packagePrice; // Force override for tests that vary it.
        await fx.DbContext.SaveChangesAsync();

        SmartFuture.Domain.NetworkAccounts.NetworkAccount? na = null;
        if (naStatus.HasValue)
        {
            na = TestEntityFactory.CreateNetworkAccount(fx.AppDbContext, order,
                status: naStatus.Value, packageType: packageType, packagePrice: packagePrice);
            await fx.DbContext.SaveChangesAsync();
        }

        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order,
            totalAmount: packagePrice,
            dueAtUtc: null,
            issuedAtUtc: null,
            status: InvoiceStatus.Paid,
            periodStartUtc: null,
            periodEndUtc: invoicePeriodEndUtc);
        if (addServicePackageLine)
            TestEntityFactory.AddLineItem(fx.AppDbContext, invoice, InvoiceLineItemType.ServicePackage, packagePrice, "Monthly");
        if (addProRataLine)
            TestEntityFactory.AddLineItem(fx.AppDbContext, invoice, InvoiceLineItemType.ProRata, packagePrice, "Pro-rata");
        if (addInstallationLine)
            TestEntityFactory.AddLineItem(fx.AppDbContext, invoice, InvoiceLineItemType.InstallationFee, 999m, "Installation");
        await fx.DbContext.SaveChangesAsync();

        return (fx, user, order, na!, invoice);
    }

    // ─── Happy path ────────────────────────────────────────────────

    [Fact]
    public async Task ServiceBillingScheduleService_PaidServiceInvoice_CreatesSchedule()
    {
        // Fibre order, ProRata-only invoice with an explicit PeriodEndUtc
        // (matches the AdminActivateServiceAsync path). Schedule should
        // land Active anchored on the customer's billing day.
        var billingDay = 15;
        var (fx, user, order, na, invoice) = await SeedPaidInvoiceAsync(
            packageType: ServicePackageType.Fibre,
            preferredBillingDay: billingDay,
            invoicePeriodEndUtc: new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            addProRataLine: true);
        var svc = Build(fx, daysBeforeDue: 5);

        await svc.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, paidAtUtc: new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc));

        var schedule = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().SingleAsync();
        schedule.Status.Should().Be(ServiceBillingScheduleStatus.Active);
        schedule.IsAutoBillable.Should().BeTrue();
        schedule.NetworkAccountId.Should().Be(na.Id);
        schedule.OrderId.Should().Be(order.Id);
        schedule.UserId.Should().Be(user.Id);
        schedule.AnchorDayOfMonth.Should().Be(billingDay);
        schedule.CurrentPeriodStartUtc.Should().Be(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        schedule.NextDueDateUtc.Should().Be(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        schedule.NextInvoiceDateUtc.Should().Be(new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc)); // 15 - 5 lead
        schedule.LastInvoicedPeriodEndUtc.Should().Be(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        schedule.LastInvoiceId.Should().Be(invoice.Id);
        schedule.BillingCycle.Should().Be(ServicePackageBillingCycle.Monthly);
        schedule.CurrencyCode.Should().Be("ZAR");

        var orderReloaded = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        orderReloaded.NextPayDateUtc.Should().Be(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));

        await fx.DisposeAsync();
    }

    // ─── Non-service invoice ──────────────────────────────────────

    [Fact]
    public async Task ServiceBillingScheduleService_NonServiceInvoice_DoesNothing()
    {
        // Installation-fee-only invoice — no schedule should be created.
        var (fx, _, _, _, invoice) = await SeedPaidInvoiceAsync(
            addProRataLine: false,
            addServicePackageLine: false,
            addInstallationLine: true);
        var svc = Build(fx);

        await svc.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, paidAtUtc: DateTime.SpecifyKind(new DateTime(2026, 7, 8), DateTimeKind.Utc));

        (await fx.DbContext.ServiceBillingSchedules.CountAsync()).Should().Be(0);

        await fx.DisposeAsync();
    }

    // ─── Idempotency ───────────────────────────────────────────────

    [Fact]
    public async Task ServiceBillingScheduleService_ExistingSchedule_IsIdempotent()
    {
        // First call creates the schedule; second call must NOT create
        // a second one AND must not re-anchor the existing one.
        var (fx, user, order, na, invoice) = await SeedPaidInvoiceAsync(
            invoicePeriodEndUtc: new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        var svc = Build(fx);

        var paidAt = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        await svc.EnsureActivatedForPaidServiceInvoiceAsync(invoice.Id, paidAt);
        var first = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().SingleAsync();

        // Second call, different paidAt — must be a no-op.
        var laterPaidAt = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        await svc.EnsureActivatedForPaidServiceInvoiceAsync(invoice.Id, laterPaidAt);

        var schedules = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().ToListAsync();
        schedules.Should().HaveCount(1);
        schedules.Single().CurrentPeriodStartUtc.Should().Be(first.CurrentPeriodStartUtc,
            "schedule must not re-anchor on a subsequent paid invoice");
        schedules.Single().NextDueDateUtc.Should().Be(first.NextDueDateUtc);

        await fx.DisposeAsync();
    }

    // ─── Missing NetworkAccount ────────────────────────────────────

    [Fact]
    public async Task ServiceBillingScheduleService_MissingNetworkAccount_QuietlyDoesNothing()
    {
        // Race with the applier's provisioning hook — no NA yet. The
        // service must NOT throw, and must NOT create a schedule; the
        // next paid service invoice will retry.
        var (fx, _, _, _, invoice) = await SeedPaidInvoiceAsync(
            naStatus: null,
            invoicePeriodEndUtc: new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        var svc = Build(fx);

        await svc.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, paidAtUtc: new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc));

        (await fx.DbContext.ServiceBillingSchedules.CountAsync()).Should().Be(0);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ServiceBillingScheduleService_TerminatedNetworkAccount_QuietlyDoesNothing()
    {
        // Terminated NA is excluded from the "most recent non-terminated"
        // pick — schedule not created.
        var (fx, _, _, _, invoice) = await SeedPaidInvoiceAsync(
            naStatus: NetworkAccountStatus.Terminated,
            invoicePeriodEndUtc: new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        var svc = Build(fx);

        await svc.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, paidAtUtc: new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc));

        (await fx.DbContext.ServiceBillingSchedules.CountAsync()).Should().Be(0);

        await fx.DisposeAsync();
    }

    // ─── Snapshot rules ───────────────────────────────────────────

    [Fact]
    public async Task ServiceBillingScheduleService_UsesOrderPreferredBillingDay()
    {
        // Test days 15, 25, 30 — schedule anchor + next-due must all
        // agree with Order.PreferredBillingDay.
        foreach (var billingDay in new[] { 15, 25, 30 })
        {
            var (fx, _, _, _, invoice) = await SeedPaidInvoiceAsync(
                preferredBillingDay: billingDay,
                invoicePeriodEndUtc: null); // Force ProRataCalculator path
            var svc = Build(fx);

            var paidAt = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
            await svc.EnsureActivatedForPaidServiceInvoiceAsync(invoice.Id, paidAt);

            var schedule = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().SingleAsync();
            schedule.AnchorDayOfMonth.Should().Be(billingDay);

            await fx.DisposeAsync();
        }
    }

    [Fact]
    public async Task ServiceBillingScheduleService_UsesOrderPackagePriceSnapshot()
    {
        // The order snapshotted PackagePrice at conversion. The schedule
        // uses that snapshot, NOT the current ServicePackage.Price. Even
        // if admin later bumps the package price, the schedule stays.
        var (fx, _, order, _, invoice) = await SeedPaidInvoiceAsync(
            packagePrice: 699m,
            invoicePeriodEndUtc: new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        var pkg = await fx.DbContext.ServicePackages.SingleAsync();
        pkg.Price = 999m;                       // Admin bumps AFTER order was placed
        await fx.DbContext.SaveChangesAsync();

        var svc = Build(fx);
        await svc.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, paidAtUtc: new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc));

        var schedule = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().SingleAsync();
        schedule.Amount.Should().Be(699m,
            "schedule must lock to Order.PackagePrice snapshot, not the current ServicePackage price");

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ServiceBillingScheduleService_SecurityVariantOrder_UsesSnapshottedVariantPrice()
    {
        // Simulate the intent-conversion snapshot: PackagePrice was
        // snapshotted from the variant. Even if the customer's variant
        // maps to a different current base package price, the schedule
        // uses the snapshot on the order.
        var (fx, _, order, _, invoice) = await SeedPaidInvoiceAsync(
            packageType: ServicePackageType.Security,
            packagePrice: 1499m,
            invoicePeriodEndUtc: new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        var svc = Build(fx);

        await svc.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, paidAtUtc: new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc));

        var schedule = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().SingleAsync();
        schedule.Amount.Should().Be(1499m);

        await fx.DisposeAsync();
    }

    // ─── Anchor rules ──────────────────────────────────────────────

    [Fact]
    public async Task ServiceBillingScheduleService_FibreOrder_AnchorsAfterActivationInvoice()
    {
        // Fibre pro-rata invoice carries an explicit PeriodEndUtc = the
        // customer's next billing day. Schedule anchors to it verbatim.
        var (fx, _, _, _, invoice) = await SeedPaidInvoiceAsync(
            packageType: ServicePackageType.Fibre,
            preferredBillingDay: 15,
            invoicePeriodEndUtc: new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        var svc = Build(fx);

        await svc.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, paidAtUtc: new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc));

        var schedule = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().SingleAsync();
        schedule.CurrentPeriodStartUtc.Should().Be(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        schedule.CurrentPeriodEndUtc.Should().Be(new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc)); // +30 days

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ServiceBillingScheduleService_MonthEndBilling_UsesBillingDay30()
    {
        // Billing day 30, no invoice PeriodEndUtc → next billing date
        // comes from ProRataCalculator.NextBillingDate. Anchor day must
        // still be 30 on the schedule.
        var (fx, _, _, _, invoice) = await SeedPaidInvoiceAsync(
            preferredBillingDay: 30,
            invoicePeriodEndUtc: null);
        var svc = Build(fx);

        var paidAt = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        await svc.EnsureActivatedForPaidServiceInvoiceAsync(invoice.Id, paidAt);

        var schedule = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().SingleAsync();
        schedule.AnchorDayOfMonth.Should().Be(30);
        // Fell back to ProRataCalculator path — 2026-07-30 is the next 30th
        schedule.CurrentPeriodStartUtc.Should().Be(new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc));

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ServiceBillingScheduleService_LatePayment_DoesNotShiftCadenceForward()
    {
        // Customer pays on the 29th, billing day is the 15th — schedule
        // must anchor on the PeriodEndUtc (Jul 15) not on paidAt.
        var (fx, _, _, _, invoice) = await SeedPaidInvoiceAsync(
            preferredBillingDay: 15,
            invoicePeriodEndUtc: new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        var svc = Build(fx);

        var lateBusinessDayPaid = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        await svc.EnsureActivatedForPaidServiceInvoiceAsync(invoice.Id, lateBusinessDayPaid);

        var schedule = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().SingleAsync();
        schedule.NextDueDateUtc.Should().Be(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            "late payment must not shift the cadence — it anchors on the invoice's PeriodEndUtc");

        await fx.DisposeAsync();
    }
}

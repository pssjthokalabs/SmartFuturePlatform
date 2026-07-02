using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase 2 DB integration tests for RecurringInvoiceGenerator. The
// generator's contract is:
//
//   • Emit an Invoice + one ServicePackage line item for each due
//     ServiceBillingSchedule whose window has opened.
//   • Honour GenerateInvoicesDaysBeforeDue for scheduling the NEXT
//     window (advance NextInvoiceDateUtc = periodEnd - N days).
//   • Never emit a duplicate for the same (schedule, periodStart).
//   • Skip paused/cancelled schedules and inactive NetworkAccounts.
//   • Use the schedule's Amount snapshot (which is seeded from the
//     Order.PackagePrice snapshot upstream — see Phase 2 audit note).
//
// The generator lives inside the Application layer and only touches
// IAppDbContext, IOptions<AutoBillingSettings>, IBillingNotificationService,
// and ILogger — every test wires a fresh SQLite DB, a strict-mock
// notifier, and a NullLogger.
public class RecurringInvoiceGeneratorTests
{
    private static readonly DateTime BaseDayUtc = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RecurringInvoiceGenerator BuildGenerator(
        SqliteTestDbFixture fx,
        int daysBeforeDue = 5,
        int maxInvoicesPerRun = 100,
        Mock<IBillingNotificationService>? notifierMock = null)
    {
        var settings = new AutoBillingSettings
        {
            GenerateInvoicesDaysBeforeDue = daysBeforeDue,
            MaxInvoicesPerRun = maxInvoicesPerRun,
        };
        notifierMock ??= new Mock<IBillingNotificationService>(MockBehavior.Loose);
        return new RecurringInvoiceGenerator(
            fx.AppDbContext,
            Options.Create(settings),
            notifierMock.Object,
            NullLogger<RecurringInvoiceGenerator>.Instance);
    }

    private static RecurringBillingRunContext RunContext(DateTime nowUtc, bool dryRun = false) =>
        new(Guid.NewGuid(), nowUtc, dryRun, BillingRunTrigger.Scheduler, 100, 100);

    private static async Task<(Infrastructure.SqliteTestDbFixture fx,
        SmartFuture.Domain.Identity.User user,
        SmartFuture.Domain.Orders.Order order,
        SmartFuture.Domain.NetworkAccounts.NetworkAccount na)>
        SeedActiveServiceAsync(
            ServicePackageType packageType = ServicePackageType.Fibre,
            decimal packagePrice = 699m,
            NetworkAccountStatus naStatus = NetworkAccountStatus.Active)
    {
        var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext, type: packageType, price: packagePrice);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg);
        await fx.DbContext.SaveChangesAsync();
        var na = TestEntityFactory.CreateNetworkAccount(fx.AppDbContext, order, status: naStatus,
            packageType: packageType, packagePrice: packagePrice);
        await fx.DbContext.SaveChangesAsync();
        return (fx, user, order, na);
    }

    // ─── Due window / lead-time ─────────────────────────────────────

    [Fact]
    public async Task RecurringInvoiceGenerator_CreatesInvoiceConfiguredDaysBeforeDue()
    {
        // Schedule: next due 2026-08-01, so the 5-day lead time means
        // the generator becomes due at 2026-07-27. Run at exactly 07-27.
        var (fx, user, order, na) = await SeedActiveServiceAsync();
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var invoiceOpenDate = dueDate.AddDays(-5);
        TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            nextInvoiceDateUtc: invoiceOpenDate,
            nextDueDateUtc: dueDate);
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx, daysBeforeDue: 5);
        var result = await gen.GenerateDueInvoicesAsync(RunContext(invoiceOpenDate));

        result.InvoicesGenerated.Should().Be(1);
        var invoice = await fx.DbContext.Invoices.Include(i => i.LineItems).SingleAsync();
        invoice.PeriodStartUtc.Should().Be(dueDate);
        invoice.PeriodEndUtc.Should().Be(dueDate.AddDays(30));
        invoice.DueAtUtc.Should().Be(dueDate);
        invoice.TotalAmount.Should().Be(699m);
        invoice.LineItems.Should().HaveCount(1);
        invoice.LineItems.Single().LineType.Should().Be(InvoiceLineItemType.ServicePackage);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task RecurringInvoiceGenerator_DoesNotCreateBeforeWindowOpens()
    {
        // If we run 6 days before due with a 5-day lead, no invoice yet.
        var (fx, user, order, na) = await SeedActiveServiceAsync();
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            nextInvoiceDateUtc: dueDate.AddDays(-5),
            nextDueDateUtc: dueDate);
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx, daysBeforeDue: 5);
        var result = await gen.GenerateDueInvoicesAsync(RunContext(dueDate.AddDays(-6)));

        result.InvoicesGenerated.Should().Be(0);
        result.SchedulesConsidered.Should().Be(0);
        (await fx.DbContext.Invoices.CountAsync()).Should().Be(0);

        await fx.DisposeAsync();
    }

    // ─── Duplicate guard ────────────────────────────────────────────

    [Fact]
    public async Task RecurringInvoiceGenerator_DoesNotCreateDuplicateForSameCycle()
    {
        // Two runs at the same NowUtc must NOT emit two invoices for the
        // same period. First run advances the schedule past the current
        // window; second run finds nothing due AND the invoice count
        // stays at 1.
        var (fx, user, order, na) = await SeedActiveServiceAsync();
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            nextInvoiceDateUtc: dueDate.AddDays(-5),
            nextDueDateUtc: dueDate);
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx, daysBeforeDue: 5);
        var runAt = dueDate.AddDays(-5);
        await gen.GenerateDueInvoicesAsync(RunContext(runAt));
        // Second run at the same instant.
        var result2 = await gen.GenerateDueInvoicesAsync(RunContext(runAt));

        result2.InvoicesGenerated.Should().Be(0);
        (await fx.DbContext.Invoices.CountAsync()).Should().Be(1);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task RecurringInvoiceGenerator_UsesSchedulePriceSnapshot_LocksExistingBehaviour()
    {
        // Business decision required: should existing services keep the
        // original price snapshot on the schedule, or re-resolve to the
        // latest variant / package price at each cycle? Currently the
        // schedule's `Amount` field is authoritative — this test locks
        // that behaviour so a change gets caught with a red test rather
        // than a silent regression.
        //
        // (Order.PackagePrice snapshot is copied to schedule.Amount at
        // schedule-creation time; the generator reads schedule.Amount.)
        var (fx, user, order, na) = await SeedActiveServiceAsync(packagePrice: 699m);
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            nextInvoiceDateUtc: dueDate.AddDays(-5),
            nextDueDateUtc: dueDate);
        await fx.DbContext.SaveChangesAsync();

        // Admin bumps the *current* package price mid-contract.
        var pkg = await fx.DbContext.ServicePackages.SingleAsync();
        pkg.Price = 999m;
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx);
        await gen.GenerateDueInvoicesAsync(RunContext(dueDate.AddDays(-5)));

        var invoice = await fx.DbContext.Invoices.SingleAsync();
        invoice.TotalAmount.Should().Be(699m,
            "monthly invoices lock to the schedule Amount snapshot at activation; " +
            "admin package-price changes must not retroactively re-price existing services");

        await fx.DisposeAsync();
    }

    // ─── Status filters ─────────────────────────────────────────────

    [Fact]
    public async Task RecurringInvoiceGenerator_SkipsPausedServices()
    {
        var (fx, user, order, na) = await SeedActiveServiceAsync();
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            nextInvoiceDateUtc: dueDate.AddDays(-5),
            nextDueDateUtc: dueDate,
            status: ServiceBillingScheduleStatus.Paused);
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx);
        var result = await gen.GenerateDueInvoicesAsync(RunContext(dueDate.AddDays(-5)));

        result.SchedulesConsidered.Should().Be(0);
        (await fx.DbContext.Invoices.CountAsync()).Should().Be(0);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task RecurringInvoiceGenerator_SkipsCancelledServices()
    {
        var (fx, user, order, na) = await SeedActiveServiceAsync();
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            nextInvoiceDateUtc: dueDate.AddDays(-5),
            nextDueDateUtc: dueDate,
            status: ServiceBillingScheduleStatus.Cancelled);
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx);
        var result = await gen.GenerateDueInvoicesAsync(RunContext(dueDate.AddDays(-5)));

        result.SchedulesConsidered.Should().Be(0);
        (await fx.DbContext.Invoices.CountAsync()).Should().Be(0);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task RecurringInvoiceGenerator_SkipsWhenNetworkAccountNotActive()
    {
        // Schedule is Active but the NetworkAccount is Suspended.
        // Cheap-filter DOES select the schedule but per-item guard
        // increments InactiveSkipped and emits nothing.
        var (fx, user, order, na) = await SeedActiveServiceAsync(naStatus: NetworkAccountStatus.Suspended);
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            nextInvoiceDateUtc: dueDate.AddDays(-5),
            nextDueDateUtc: dueDate);
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx);
        var result = await gen.GenerateDueInvoicesAsync(RunContext(dueDate.AddDays(-5)));

        result.SchedulesConsidered.Should().Be(1);
        result.InactiveSkipped.Should().Be(1);
        result.InvoicesGenerated.Should().Be(0);

        await fx.DisposeAsync();
    }

    // ─── Advance / next window ──────────────────────────────────────

    [Fact]
    public async Task RecurringInvoiceGenerator_AdvancesScheduleToNextPeriod()
    {
        var (fx, user, order, na) = await SeedActiveServiceAsync();
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var schedule = TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            nextInvoiceDateUtc: dueDate.AddDays(-5),
            nextDueDateUtc: dueDate);
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx, daysBeforeDue: 5);
        await gen.GenerateDueInvoicesAsync(RunContext(dueDate.AddDays(-5)));

        var loaded = await fx.DbContext.ServiceBillingSchedules.AsNoTracking().SingleAsync(s => s.Id == schedule.Id);
        loaded.NextDueDateUtc.Should().Be(dueDate.AddDays(30));
        loaded.NextInvoiceDateUtc.Should().Be(dueDate.AddDays(30).AddDays(-5));
        loaded.LastInvoicedPeriodEndUtc.Should().Be(dueDate.AddDays(30));
        loaded.LastInvoiceId.Should().NotBeNull();

        await fx.DisposeAsync();
    }

    // ─── Batch cap + dry run ────────────────────────────────────────

    [Fact]
    public async Task RecurringInvoiceGenerator_RespectsMaxInvoicesPerRunCap()
    {
        var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext);
        await fx.DbContext.SaveChangesAsync();

        // 3 due schedules, cap = 2 → only 2 emitted, Truncated=true.
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
        {
            var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg,
                orderNumber: $"SF-ORD-CAP-{i}");
            await fx.DbContext.SaveChangesAsync();
            var na = TestEntityFactory.CreateNetworkAccount(fx.AppDbContext, order);
            await fx.DbContext.SaveChangesAsync();
            TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
                amount: 699m, anchorDayOfMonth: 1,
                nextInvoiceDateUtc: dueDate.AddDays(-5),
                nextDueDateUtc: dueDate);
        }
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx, maxInvoicesPerRun: 2);
        var result = await gen.GenerateDueInvoicesAsync(RunContext(dueDate.AddDays(-5)));

        result.InvoicesGenerated.Should().Be(2);
        result.Truncated.Should().BeTrue();
        (await fx.DbContext.Invoices.CountAsync()).Should().Be(2);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task RecurringInvoiceGenerator_DryRun_ReportsButDoesNotWrite()
    {
        var (fx, user, order, na) = await SeedActiveServiceAsync();
        var dueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            nextInvoiceDateUtc: dueDate.AddDays(-5),
            nextDueDateUtc: dueDate);
        await fx.DbContext.SaveChangesAsync();

        var notifier = new Mock<IBillingNotificationService>(MockBehavior.Strict);
        // Strict mock proves nothing else is called — dry-run must never
        // send emails, even best-effort.

        var gen = BuildGenerator(fx, notifierMock: notifier);
        var result = await gen.GenerateDueInvoicesAsync(RunContext(dueDate.AddDays(-5), dryRun: true));

        result.WouldGenerate.Should().Be(1);
        result.InvoicesGenerated.Should().Be(0);
        (await fx.DbContext.Invoices.CountAsync()).Should().Be(0);
        notifier.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    // ─── Month-end / leap behaviour ─────────────────────────────────
    //
    // The RecurringInvoiceGenerator itself is period-arithmetic based
    // (period = due + 30 days). Month-end clamp happens upstream when
    // the schedule is anchored (see ProRataCalculator.NextBillingDate).
    // Here we lock the "advance by exactly the interval" invariant, then
    // spot-check that when a schedule's NextDueDate lands on a Feb 28
    // seed, the next period end is Feb 28 + 30 = 30 March (NOT March 28
    // wall-clock month-add).
    [Fact]
    public async Task RecurringInvoiceGenerator_MonthEndSeeded_AdvancesByThirtyDayInterval()
    {
        var (fx, user, order, na) = await SeedActiveServiceAsync();
        var dueDate = new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc);
        TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 28,
            nextInvoiceDateUtc: dueDate.AddDays(-5),
            nextDueDateUtc: dueDate);
        await fx.DbContext.SaveChangesAsync();

        var gen = BuildGenerator(fx);
        await gen.GenerateDueInvoicesAsync(RunContext(dueDate.AddDays(-5)));

        var invoice = await fx.DbContext.Invoices.SingleAsync();
        invoice.PeriodStartUtc.Should().Be(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc));
        invoice.PeriodEndUtc.Should().Be(new DateTime(2026, 3, 30, 0, 0, 0, DateTimeKind.Utc));

        await fx.DisposeAsync();
    }
}

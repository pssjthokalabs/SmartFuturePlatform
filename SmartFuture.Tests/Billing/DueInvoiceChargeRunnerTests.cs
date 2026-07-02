using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-4 DB integration tests for DueInvoiceChargeRunner (Stage 2 of
// the recurring billing pipeline). The runner:
//
//   1. Selects due, schedule-linked recurring invoices (Issued/Overdue/
//      PartiallyPaid with balance > 0 and DueAtUtc <= now).
//   2. Applies safety filters: paid re-check, customer opt-in, active
//      default reusable mandate (via IRecurringMandateSelector), no
//      non-stale pending PaymentInitiation, no pending PaymentRetryAttempt
//      (Stage 3 owns retry-chain invoices).
//   3. Dry-run reports WouldCharge without calling the provider.
//   4. Real run delegates to IAutoBillingService.ChargeInvoiceAsync.
//
// Provider is fully mocked — no real Paystack / PayFast calls.
public class DueInvoiceChargeRunnerTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    private static DueInvoiceChargeRunner Build(
        SqliteTestDbFixture fx,
        Mock<IAutoBillingService> autoBilling,
        Mock<IRecurringMandateSelector> mandate,
        bool enabled = true,
        bool chargeAuthEnabled = true,
        int maxChargesPerRun = 100) =>
        new(fx.AppDbContext,
            autoBilling.Object,
            mandate.Object,
            Options.Create(new AutoBillingSettings
            {
                Enabled = enabled,
                ChargeAuthorizationEnabled = chargeAuthEnabled,
                MaxChargesPerRun = maxChargesPerRun,
            }),
            NullLogger<DueInvoiceChargeRunner>.Instance);

    private static RecurringBillingRunContext Ctx(bool dryRun = false) =>
        new(Guid.NewGuid(), NowUtc, dryRun, BillingRunTrigger.Scheduler, 100, 100);

    // Full happy-path seed:
    // User + CustomerProfile(AutoBillingEnabled=true) → Package → Order (Active)
    // → NetworkAccount (Active) → Schedule (Active + auto-billable)
    // → Invoice (Issued + due + service-linked).
    private static async Task<(SqliteTestDbFixture fx,
        SmartFuture.Domain.Identity.User user,
        SmartFuture.Domain.Billing.Invoice invoice)>
        SeedDueInvoiceAsync(
            InvoiceStatus invoiceStatus = InvoiceStatus.Issued,
            DateTime? dueAtUtc = null,
            decimal balanceDue = 699m,
            bool autoBillingEnabled = true,
            NetworkAccountStatus naStatus = NetworkAccountStatus.Active,
            ServiceBillingScheduleStatus scheduleStatus = ServiceBillingScheduleStatus.Active,
            bool scheduleIsAutoBillable = true)
    {
        var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        TestEntityFactory.CreateCustomerProfile(fx.AppDbContext, user, autoBillingEnabled);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext, type: ServicePackageType.Fibre);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg);
        await fx.DbContext.SaveChangesAsync();

        var na = TestEntityFactory.CreateNetworkAccount(fx.AppDbContext, order, status: naStatus);
        await fx.DbContext.SaveChangesAsync();
        var schedule = TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1,
            status: scheduleStatus, isAutoBillable: scheduleIsAutoBillable);
        await fx.DbContext.SaveChangesAsync();

        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order,
            totalAmount: 699m,
            dueAtUtc: dueAtUtc ?? NowUtc.AddDays(-1),
            issuedAtUtc: NowUtc.AddDays(-10),
            status: invoiceStatus,
            serviceBillingScheduleId: schedule.Id,
            periodStartUtc: NowUtc.AddDays(-30),
            periodEndUtc: NowUtc.AddDays(0));
        invoice.BalanceDue = balanceDue;
        await fx.DbContext.SaveChangesAsync();

        return (fx, user, invoice);
    }

    private static Mock<IRecurringMandateSelector> AvailableMandate()
    {
        var m = new Mock<IRecurringMandateSelector>(MockBehavior.Loose);
        m.Setup(x => x.GetAvailabilityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MandateAvailability.Available);
        return m;
    }

    private static Mock<IRecurringMandateSelector> Unavailable(MandateAvailability reason)
    {
        var m = new Mock<IRecurringMandateSelector>(MockBehavior.Loose);
        m.Setup(x => x.GetAvailabilityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reason);
        return m;
    }

    // ─── Master switch ──────────────────────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_Disabled_DoesNothing()
    {
        var (fx, _, _) = await SeedDueInvoiceAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate, enabled: false);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.CandidatesSelected.Should().Be(0);
        result.ChargesAttempted.Should().Be(0);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    // ─── Dry run ───────────────────────────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_DryRun_DoesNotCallProvider()
    {
        var (fx, _, _) = await SeedDueInvoiceAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx(dryRun: true));

        result.CandidatesSelected.Should().Be(1);
        result.WouldCharge.Should().Be(1);
        result.ChargesAttempted.Should().Be(0);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    // ─── Happy path ────────────────────────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_DueInvoiceWithPaystackMandate_CallsPaystackCharge()
    {
        var (fx, _, invoice) = await SeedDueInvoiceAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Loose);
        autoBilling.Setup(x => x.ChargeInvoiceAsync(
                It.IsAny<Guid>(), It.IsAny<AutoBillingChargeSource>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AutoBillingChargeOutcome>.Success(
                new AutoBillingChargeOutcome(Charged: true, Reference: "PS-REF-1",
                    PaymentId: Guid.NewGuid(), PaymentInitiationId: Guid.NewGuid(),
                    FailureReason: null)));
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.CandidatesSelected.Should().Be(1);
        result.ChargesAttempted.Should().Be(1);
        result.ChargesSucceeded.Should().Be(1);
        autoBilling.Verify(x => x.ChargeInvoiceAsync(
            invoice.Id, AutoBillingChargeSource.MonthlyRenewal,
            null, It.IsAny<CancellationToken>()), Times.Once);

        await fx.DisposeAsync();
    }

    // ─── Mandate skips ─────────────────────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_DueInvoiceWithoutMandate_RemainsManualPay()
    {
        var (fx, _, _) = await SeedDueInvoiceAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var mandate = Unavailable(MandateAvailability.NoReusableMandate);
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.SkippedNoMandate.Should().Be(1);
        result.ChargesAttempted.Should().Be(0);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task DueInvoiceChargeRunner_PayFastRecurringDisabled_DoesNotCallPayFast()
    {
        // Mandate selector reports "PayFast recurring disabled" — matches
        // AutoBilling__EnablePayFastRecurring=false when only mandate is
        // PayFast. Runner must skip, not touch the provider.
        var (fx, _, _) = await SeedDueInvoiceAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var mandate = Unavailable(MandateAvailability.PayFastRecurringDisabled);
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.SkippedNoMandate.Should().Be(1);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task DueInvoiceChargeRunner_PayFastRecurringEnabled_CallsPayFastProvider()
    {
        // Mandate selector reports Available (irrespective of provider —
        // the selector abstracts that). Runner charges through the same
        // seam regardless of provider.
        var (fx, _, invoice) = await SeedDueInvoiceAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Loose);
        autoBilling.Setup(x => x.ChargeInvoiceAsync(
                It.IsAny<Guid>(), It.IsAny<AutoBillingChargeSource>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AutoBillingChargeOutcome>.Success(
                new AutoBillingChargeOutcome(Charged: true, Reference: "PF-REF-1",
                    PaymentId: Guid.NewGuid(), PaymentInitiationId: Guid.NewGuid(),
                    FailureReason: null)));
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.ChargesSucceeded.Should().Be(1);
        autoBilling.Verify(x => x.ChargeInvoiceAsync(
            invoice.Id, AutoBillingChargeSource.MonthlyRenewal,
            null, It.IsAny<CancellationToken>()), Times.Once);

        await fx.DisposeAsync();
    }

    // ─── Invoice status filters ────────────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_SkipsPaidInvoice()
    {
        var (fx, _, _) = await SeedDueInvoiceAsync(invoiceStatus: InvoiceStatus.Paid, balanceDue: 0m);
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.CandidatesSelected.Should().Be(0,
            "cheap-filter excludes Paid + zero-balance invoices");
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task DueInvoiceChargeRunner_SkipsNotYetDueInvoice()
    {
        // Due 5 days from now — not selected.
        var (fx, _, _) = await SeedDueInvoiceAsync(dueAtUtc: NowUtc.AddDays(5));
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.CandidatesSelected.Should().Be(0);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    // ─── Opt-in skip ───────────────────────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_SkipsWhenCustomerNotOptedIn()
    {
        var (fx, _, _) = await SeedDueInvoiceAsync(autoBillingEnabled: false);
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.SkippedNoOptIn.Should().Be(1);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    // ─── Pending retry blocks Stage 2 ──────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_SkipsWhenRetryChainInFlight()
    {
        // Stage 3 (RetryRunner) owns retry-chain invoices exclusively.
        var (fx, user, invoice) = await SeedDueInvoiceAsync();
        TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice, user, attemptNumber: 1,
            status: PaymentRetryAttemptStatus.Pending);
        await fx.DbContext.SaveChangesAsync();

        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.SkippedPendingRetry.Should().Be(1);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    // ─── Cap ───────────────────────────────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_RespectsMaxChargesPerRun()
    {
        // Two due invoices, cap=1 → one processed, second truncated.
        var (fx, user, invoice1) = await SeedDueInvoiceAsync();

        // Add a second due invoice on a different order.
        var pkg2 = TestEntityFactory.CreateServicePackage(fx.AppDbContext, name: "Pkg 2");
        var order2 = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg2, orderNumber: "SF-ORD-B");
        await fx.DbContext.SaveChangesAsync();
        var na2 = TestEntityFactory.CreateNetworkAccount(fx.AppDbContext, order2);
        await fx.DbContext.SaveChangesAsync();
        var sched2 = TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na2, order2, user,
            amount: 699m, anchorDayOfMonth: 1);
        await fx.DbContext.SaveChangesAsync();
        var invoice2 = TestEntityFactory.CreateInvoice(fx.AppDbContext, order2, totalAmount: 699m,
            dueAtUtc: NowUtc.AddDays(-2), status: InvoiceStatus.Issued,
            serviceBillingScheduleId: sched2.Id);
        await fx.DbContext.SaveChangesAsync();

        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Loose);
        autoBilling.Setup(x => x.ChargeInvoiceAsync(
                It.IsAny<Guid>(), It.IsAny<AutoBillingChargeSource>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AutoBillingChargeOutcome>.Success(
                new AutoBillingChargeOutcome(true, "R", Guid.NewGuid(), Guid.NewGuid(), null)));
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate, maxChargesPerRun: 1);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.CandidatesSelected.Should().Be(2);
        result.ChargesAttempted.Should().Be(1);
        result.Truncated.Should().BeTrue();
        autoBilling.Verify(x => x.ChargeInvoiceAsync(
            It.IsAny<Guid>(), It.IsAny<AutoBillingChargeSource>(),
            It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);

        await fx.DisposeAsync();
    }

    // ─── Provider failure ──────────────────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_ProviderFailure_CountsFailureAndContinues()
    {
        var (fx, _, invoice) = await SeedDueInvoiceAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Loose);
        autoBilling.Setup(x => x.ChargeInvoiceAsync(
                It.IsAny<Guid>(), It.IsAny<AutoBillingChargeSource>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AutoBillingChargeOutcome>.Success(
                new AutoBillingChargeOutcome(Charged: false, Reference: null,
                    PaymentId: null, PaymentInitiationId: null,
                    FailureReason: "insufficient_funds")));
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.ChargesAttempted.Should().Be(1);
        result.ChargesSucceeded.Should().Be(0);
        result.ChargesFailed.Should().Be(1);
        result.ErrorCount.Should().Be(0,
            "a provider-reported failure is NOT the same as a thrown exception");

        await fx.DisposeAsync();
    }

    // ─── Charge-auth disabled ──────────────────────────────────────

    [Fact]
    public async Task DueInvoiceChargeRunner_ChargeAuthDisabled_RealRun_SkipsWithoutCall()
    {
        var (fx, _, _) = await SeedDueInvoiceAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var mandate = AvailableMandate();
        var runner = Build(fx, autoBilling, mandate, chargeAuthEnabled: false);

        var result = await runner.ChargeDueInvoicesAsync(Ctx());

        result.SkippedChargeAuthDisabled.Should().Be(1);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }
}

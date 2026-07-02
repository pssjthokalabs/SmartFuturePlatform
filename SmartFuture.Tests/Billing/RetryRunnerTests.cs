using Microsoft.EntityFrameworkCore;
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

// Phase-4 DB integration tests for RetryRunner (Stage 3 of the recurring
// billing pipeline). The runner:
//
//   1. Selects due, Pending PaymentRetryAttempt rows.
//   2. Skips paid/settled invoices (marks the attempt Skipped).
//   3. Skips attempts past MaxRetryAttempts (marks Skipped).
//   4. Skips no-opt-in / no-mandate / pending-initiation / inactive-
//      service invoices (transient — attempt stays Pending).
//   5. Dry-run reports WouldRetry, no mutation, no provider call.
//   6. Real run delegates to IAutoBillingService.ChargeInvoiceAsync in
//      REUSE mode (passing the attempt id).
//
// Provider is fully mocked.
public class RetryRunnerTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    private static RetryRunner Build(
        SqliteTestDbFixture fx,
        Mock<IAutoBillingService> autoBilling,
        Mock<IRecurringMandateSelector> mandate,
        bool enabled = true,
        bool retryJobEnabled = true,
        bool chargeAuthEnabled = true,
        int maxRetryAttempts = 3,
        int maxChargesPerRun = 100) =>
        new(fx.AppDbContext,
            autoBilling.Object,
            mandate.Object,
            Options.Create(new AutoBillingSettings
            {
                Enabled = enabled,
                RetryJobEnabled = retryJobEnabled,
                ChargeAuthorizationEnabled = chargeAuthEnabled,
                MaxRetryAttempts = maxRetryAttempts,
                MaxChargesPerRun = maxChargesPerRun,
            }),
            NullLogger<RetryRunner>.Instance);

    private static RecurringBillingRunContext Ctx(bool dryRun = false) =>
        new(Guid.NewGuid(), NowUtc, dryRun, BillingRunTrigger.Scheduler, 100, 100);

    private static async Task<(SqliteTestDbFixture fx,
        SmartFuture.Domain.Identity.User user,
        SmartFuture.Domain.Billing.Invoice invoice,
        SmartFuture.Domain.Billing.PaymentRetryAttempt attempt)>
        SeedDuePendingRetryAsync(
            int attemptNumber = 2,
            DateTime? scheduledForUtc = null,
            InvoiceStatus invoiceStatus = InvoiceStatus.Issued,
            decimal balanceDue = 699m,
            bool autoBillingEnabled = true,
            NetworkAccountStatus naStatus = NetworkAccountStatus.Active)
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
            amount: 699m, anchorDayOfMonth: 1);
        await fx.DbContext.SaveChangesAsync();

        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order,
            totalAmount: 699m,
            dueAtUtc: NowUtc.AddDays(-3),
            issuedAtUtc: NowUtc.AddDays(-10),
            status: invoiceStatus,
            serviceBillingScheduleId: schedule.Id);
        invoice.BalanceDue = balanceDue;
        await fx.DbContext.SaveChangesAsync();

        var attempt = TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice, user,
            attemptNumber, status: PaymentRetryAttemptStatus.Pending,
            attemptedAtUtc: scheduledForUtc ?? NowUtc.AddHours(-1));
        // Override the scheduling for the runner selection.
        attempt.ScheduledForUtc = scheduledForUtc ?? NowUtc.AddHours(-1);
        await fx.DbContext.SaveChangesAsync();

        return (fx, user, invoice, attempt);
    }

    private static Mock<IRecurringMandateSelector> AvailableMandate()
    {
        var m = new Mock<IRecurringMandateSelector>(MockBehavior.Loose);
        m.Setup(x => x.GetAvailabilityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MandateAvailability.Available);
        return m;
    }

    // ─── Master switches ───────────────────────────────────────────

    [Fact]
    public async Task RetryRunner_Disabled_DoesNothing()
    {
        var (fx, _, _, _) = await SeedDuePendingRetryAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var runner = Build(fx, autoBilling, AvailableMandate(), enabled: false);

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.AttemptsSelected.Should().Be(0);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task RetryRunner_RetryJobDisabled_DoesNothing()
    {
        var (fx, _, _, _) = await SeedDuePendingRetryAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var runner = Build(fx, autoBilling, AvailableMandate(), retryJobEnabled: false);

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.AttemptsSelected.Should().Be(0);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    // ─── Dry run ───────────────────────────────────────────────────

    [Fact]
    public async Task RetryRunner_DryRun_DoesNotCallProvider()
    {
        var (fx, _, _, attempt) = await SeedDuePendingRetryAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var runner = Build(fx, autoBilling, AvailableMandate());

        var result = await runner.RunDueRetriesAsync(Ctx(dryRun: true));

        result.AttemptsSelected.Should().Be(1);
        result.WouldRetry.Should().Be(1);
        result.RetriesAttempted.Should().Be(0);
        autoBilling.VerifyNoOtherCalls();

        var reloaded = await fx.DbContext.PaymentRetryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        reloaded.Status.Should().Be(PaymentRetryAttemptStatus.Pending,
            "dry-run must not mutate attempt state");

        await fx.DisposeAsync();
    }

    // ─── Happy path ────────────────────────────────────────────────

    [Fact]
    public async Task RetryRunner_DueRetry_CallsProviderInReuseMode()
    {
        var (fx, _, invoice, attempt) = await SeedDuePendingRetryAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Loose);
        autoBilling.Setup(x => x.ChargeInvoiceAsync(
                It.IsAny<Guid>(), It.IsAny<AutoBillingChargeSource>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AutoBillingChargeOutcome>.Success(
                new AutoBillingChargeOutcome(true, "R-2", Guid.NewGuid(), Guid.NewGuid(), null)));
        var runner = Build(fx, autoBilling, AvailableMandate());

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.RetriesAttempted.Should().Be(1);
        result.RetriesSucceeded.Should().Be(1);
        autoBilling.Verify(x => x.ChargeInvoiceAsync(
            invoice.Id, AutoBillingChargeSource.Retry,
            attempt.Id, It.IsAny<CancellationToken>()), Times.Once,
            "runner must pass the attempt id so the charge service REUSES the row instead of creating a new one");

        await fx.DisposeAsync();
    }

    // ─── Time-based filter ────────────────────────────────────────

    [Fact]
    public async Task RetryRunner_NotYetDueRetry_IsSkipped()
    {
        // Attempt scheduled 5 hours in the future.
        var (fx, _, _, _) = await SeedDuePendingRetryAsync(scheduledForUtc: NowUtc.AddHours(5));
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var runner = Build(fx, autoBilling, AvailableMandate());

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.AttemptsSelected.Should().Be(0);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }

    // ─── Terminal skips ────────────────────────────────────────────

    [Fact]
    public async Task RetryRunner_MaxAttemptsReached_StopsRetrying_AndMarksSkipped()
    {
        // Attempt number 4 with MaxRetryAttempts=3 → over the limit.
        var (fx, _, _, attempt) = await SeedDuePendingRetryAsync(attemptNumber: 4);
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var runner = Build(fx, autoBilling, AvailableMandate(), maxRetryAttempts: 3);

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.SkippedMaxAttempts.Should().Be(1);
        result.RetriesAttempted.Should().Be(0);
        autoBilling.VerifyNoOtherCalls();

        var reloaded = await fx.DbContext.PaymentRetryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        reloaded.Status.Should().Be(PaymentRetryAttemptStatus.Skipped,
            "max-attempts is a TERMINAL skip — the attempt must be marked Skipped so it doesn't reappear");
        reloaded.FailureReason.Should().Contain("max_attempts_exceeded");

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task RetryRunner_InvoiceAlreadyPaid_MarksAttemptSkipped()
    {
        var (fx, _, _, attempt) = await SeedDuePendingRetryAsync(
            invoiceStatus: InvoiceStatus.Paid, balanceDue: 0m);
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var runner = Build(fx, autoBilling, AvailableMandate());

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.SkippedPaidOrSettled.Should().Be(1);
        var reloaded = await fx.DbContext.PaymentRetryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        reloaded.Status.Should().Be(PaymentRetryAttemptStatus.Skipped);

        await fx.DisposeAsync();
    }

    // ─── Transient skips (leave Pending) ──────────────────────────

    [Fact]
    public async Task RetryRunner_NoOptIn_LeavesAttemptPending()
    {
        var (fx, _, _, attempt) = await SeedDuePendingRetryAsync(autoBillingEnabled: false);
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var runner = Build(fx, autoBilling, AvailableMandate());

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.SkippedNoOptIn.Should().Be(1);
        var reloaded = await fx.DbContext.PaymentRetryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        reloaded.Status.Should().Be(PaymentRetryAttemptStatus.Pending,
            "no-opt-in is TRANSIENT — attempt stays Pending, resumes when the customer opts back in");

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task RetryRunner_NoMandate_LeavesAttemptPending()
    {
        var (fx, _, _, attempt) = await SeedDuePendingRetryAsync();
        var noMandate = new Mock<IRecurringMandateSelector>(MockBehavior.Loose);
        noMandate.Setup(x => x.GetAvailabilityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MandateAvailability.NoReusableMandate);
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var runner = Build(fx, autoBilling, noMandate);

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.SkippedNoMandate.Should().Be(1);
        var reloaded = await fx.DbContext.PaymentRetryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        reloaded.Status.Should().Be(PaymentRetryAttemptStatus.Pending);

        await fx.DisposeAsync();
    }

    // ─── Cap ───────────────────────────────────────────────────────

    [Fact]
    public async Task RetryRunner_RespectsMaxChargesPerRun()
    {
        // Two due attempts (on DIFFERENT invoices), cap=1.
        var (fx, user, _, _) = await SeedDuePendingRetryAsync();

        // Add a second invoice + attempt for the same user.
        var pkg2 = TestEntityFactory.CreateServicePackage(fx.AppDbContext, name: "P2");
        var order2 = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg2, orderNumber: "SF-B");
        await fx.DbContext.SaveChangesAsync();
        var na2 = TestEntityFactory.CreateNetworkAccount(fx.AppDbContext, order2);
        await fx.DbContext.SaveChangesAsync();
        var sched2 = TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na2, order2, user,
            amount: 699m, anchorDayOfMonth: 1);
        await fx.DbContext.SaveChangesAsync();
        var invoice2 = TestEntityFactory.CreateInvoice(fx.AppDbContext, order2, totalAmount: 699m,
            dueAtUtc: NowUtc.AddDays(-3), status: InvoiceStatus.Issued,
            serviceBillingScheduleId: sched2.Id);
        await fx.DbContext.SaveChangesAsync();
        var attempt2 = TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice2, user,
            attemptNumber: 2, status: PaymentRetryAttemptStatus.Pending,
            attemptedAtUtc: NowUtc.AddHours(-2));
        attempt2.ScheduledForUtc = NowUtc.AddHours(-2);
        await fx.DbContext.SaveChangesAsync();

        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Loose);
        autoBilling.Setup(x => x.ChargeInvoiceAsync(
                It.IsAny<Guid>(), It.IsAny<AutoBillingChargeSource>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AutoBillingChargeOutcome>.Success(
                new AutoBillingChargeOutcome(true, "R", Guid.NewGuid(), Guid.NewGuid(), null)));
        var runner = Build(fx, autoBilling, AvailableMandate(), maxChargesPerRun: 1);

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.RetriesAttempted.Should().Be(1);
        result.Truncated.Should().BeTrue();
        autoBilling.Verify(x => x.ChargeInvoiceAsync(
            It.IsAny<Guid>(), It.IsAny<AutoBillingChargeSource>(),
            It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);

        await fx.DisposeAsync();
    }

    // ─── Failure path ──────────────────────────────────────────────

    [Fact]
    public async Task RetryRunner_FailedRetry_CountsFailureAndContinues()
    {
        var (fx, _, _, _) = await SeedDuePendingRetryAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Loose);
        autoBilling.Setup(x => x.ChargeInvoiceAsync(
                It.IsAny<Guid>(), It.IsAny<AutoBillingChargeSource>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AutoBillingChargeOutcome>.Success(
                new AutoBillingChargeOutcome(false, null, null, null, "auth_failed")));
        var runner = Build(fx, autoBilling, AvailableMandate());

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.RetriesAttempted.Should().Be(1);
        result.RetriesFailed.Should().Be(1);
        result.RetriesSucceeded.Should().Be(0);
        // The charge service owns creating the NEXT attempt on failure —
        // RetryRunner just reports.

        await fx.DisposeAsync();
    }

    // ─── Charge-auth disabled ──────────────────────────────────────

    [Fact]
    public async Task RetryRunner_ChargeAuthDisabled_SkipsWithoutCall()
    {
        var (fx, _, _, _) = await SeedDuePendingRetryAsync();
        var autoBilling = new Mock<IAutoBillingService>(MockBehavior.Strict);
        var runner = Build(fx, autoBilling, AvailableMandate(), chargeAuthEnabled: false);

        var result = await runner.RunDueRetriesAsync(Ctx());

        result.SkippedChargeAuthDisabled.Should().Be(1);
        autoBilling.VerifyNoOtherCalls();

        await fx.DisposeAsync();
    }
}

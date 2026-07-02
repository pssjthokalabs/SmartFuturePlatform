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
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase 2 DB integration tests for GraceSuspensionRunner.
//
// The runner is deliberately REPORT-ONLY in the current phase: it
// identifies suspension candidates, logs them, and may send an internal
// alert — but it never mutates a NetworkAccount. These tests lock the
// selection invariants so a code change that either (a) starts
// suspending accidentally, or (b) drops a case from selection, fails
// with a clean red test.
//
// Selection contract (from Phase-1 audit + current code):
//   • Auto-billing must be Enabled (settings.Enabled=true).
//   • Invoice is service-linked (ServiceBillingScheduleId != null),
//     BalanceDue > 0, Status in {Issued, Overdue, PartiallyPaid}.
//   • Invoice.DueAtUtc + GracePeriodDays < now — i.e. grace expired.
//   • Schedule Active + IsAutoBillable AND NetworkAccount Active.
//   • Live re-check: still unpaid and grace still expired.
//   • Retry chain exhausted: max attempt >= AutoBillingSettings.MaxRetryAttempts,
//     no Pending retry left.
//   • No non-stale Pending PaymentInitiation.
public class GraceSuspensionRunnerTests
{
    private static GraceSuspensionRunner BuildRunner(
        SqliteTestDbFixture fx,
        int gracePeriodDays = 3,
        int maxRetryAttempts = 3,
        bool enabled = true,
        Mock<IBillingNotificationService>? notifierMock = null)
    {
        var settings = new AutoBillingSettings
        {
            Enabled = enabled,
            GracePeriodDays = gracePeriodDays,
            MaxRetryAttempts = maxRetryAttempts,
            SuspendAfterGracePeriodEnabled = false,
            MaxSuspensionCandidatesPerRun = 500,
        };
        notifierMock ??= new Mock<IBillingNotificationService>(MockBehavior.Loose);
        return new GraceSuspensionRunner(
            fx.AppDbContext,
            Options.Create(settings),
            notifierMock.Object,
            NullLogger<GraceSuspensionRunner>.Instance);
    }

    private static RecurringBillingRunContext RunContext(DateTime nowUtc) =>
        new(Guid.NewGuid(), nowUtc, DryRun: false, BillingRunTrigger.Scheduler, 100, 100);

    // Seeds an ACTIVE service + one recurring invoice whose due date /
    // status the caller sets. Returns everything the tests need to
    // decorate the graph with retry attempts / initiations.
    private static async Task<(SqliteTestDbFixture fx,
        SmartFuture.Domain.Identity.User user,
        SmartFuture.Domain.Billing.Invoice invoice,
        SmartFuture.Domain.Billing.ServiceBillingSchedule schedule,
        SmartFuture.Domain.NetworkAccounts.NetworkAccount networkAccount)>
        SeedInvoiceInGraceAsync(
            DateTime dueDateUtc,
            decimal balanceDue = 699m,
            InvoiceStatus invoiceStatus = InvoiceStatus.Issued,
            NetworkAccountStatus naStatus = NetworkAccountStatus.Active,
            ServiceBillingScheduleStatus scheduleStatus = ServiceBillingScheduleStatus.Active,
            bool isAutoBillable = true)
    {
        var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext, type: ServicePackageType.Fibre);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg);
        await fx.DbContext.SaveChangesAsync();
        var na = TestEntityFactory.CreateNetworkAccount(fx.AppDbContext, order, status: naStatus);
        await fx.DbContext.SaveChangesAsync();
        var schedule = TestEntityFactory.CreateBillingSchedule(fx.AppDbContext, na, order, user,
            amount: 699m, anchorDayOfMonth: 1, status: scheduleStatus, isAutoBillable: isAutoBillable);
        await fx.DbContext.SaveChangesAsync();
        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order, totalAmount: 699m,
            dueAtUtc: dueDateUtc, issuedAtUtc: dueDateUtc.AddDays(-5),
            status: invoiceStatus, serviceBillingScheduleId: schedule.Id,
            periodStartUtc: dueDateUtc, periodEndUtc: dueDateUtc.AddDays(30));
        invoice.BalanceDue = balanceDue;
        await fx.DbContext.SaveChangesAsync();
        return (fx, user, invoice, schedule, na);
    }

    // Adds N Failed retry attempts (attemptNumber 1..N) so the "retries
    // exhausted" filter is satisfied. No Pending row survives.
    private static async Task ExhaustRetryChainAsync(
        SqliteTestDbFixture fx, SmartFuture.Domain.Billing.Invoice invoice,
        SmartFuture.Domain.Identity.User customer, int attemptCount)
    {
        for (var i = 1; i <= attemptCount; i++)
        {
            TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice, customer, i,
                PaymentRetryAttemptStatus.Failed);
        }
        await fx.DbContext.SaveChangesAsync();
    }

    // ─── Grace window: not expired vs expired ───────────────────────

    [Fact]
    public async Task GraceSuspensionRunner_WithinGracePeriod_DoesNotFlagPauseCandidate()
    {
        // Due 2 days ago, grace = 3 days → still inside grace window.
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, _) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-2));
        await ExhaustRetryChainAsync(fx, invoice, user, 3);

        var runner = BuildRunner(fx, gracePeriodDays: 3);
        var result = await runner.DetectCandidatesAsync(RunContext(nowUtc));

        result.CandidatesSelected.Should().Be(0);
        result.SuspensionCandidates.Should().Be(0);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task GraceSuspensionRunner_AfterGracePeriod_FlagsPauseCandidate()
    {
        // Due 5 days ago, grace = 3 → cutoff is now-3 = 5 days later
        // than due-2. Overdue past grace → confirmed candidate.
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, _) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-5));
        await ExhaustRetryChainAsync(fx, invoice, user, 3);

        var runner = BuildRunner(fx, gracePeriodDays: 3);
        var result = await runner.DetectCandidatesAsync(RunContext(nowUtc));

        result.CandidatesSelected.Should().Be(1);
        result.SuspensionCandidates.Should().Be(1);
        // report-only: nothing was suspended
        result.Suspended.Should().Be(0);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task GraceSuspensionRunner_UsesConfiguredGracePeriodDays()
    {
        // Same invoice, two runs — one with a 1-day grace, one with a
        // 30-day grace. Only the tight grace flags it as a candidate.
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, _) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-10));
        await ExhaustRetryChainAsync(fx, invoice, user, 3);

        var runnerTight = BuildRunner(fx, gracePeriodDays: 1);
        var resultTight = await runnerTight.DetectCandidatesAsync(RunContext(nowUtc));
        resultTight.SuspensionCandidates.Should().Be(1);

        // Reset the "seen" set is per-run — a new runner instance will
        // re-detect against the same invoice. With a 30-day grace, the
        // invoice is INSIDE grace and must not be a candidate.
        var runnerGenerous = BuildRunner(fx, gracePeriodDays: 30);
        var resultGenerous = await runnerGenerous.DetectCandidatesAsync(RunContext(nowUtc));
        resultGenerous.SuspensionCandidates.Should().Be(0);

        await fx.DisposeAsync();
    }

    // ─── Non-candidate scenarios ────────────────────────────────────

    [Fact]
    public async Task GraceSuspensionRunner_PaidInvoice_NotPauseCandidate()
    {
        // Invoice is past grace but Status=Paid + BalanceDue=0 → filter
        // excludes it (cheap filter's BalanceDue > 0 gate).
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, _) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-10),
            balanceDue: 0m, invoiceStatus: InvoiceStatus.Paid);
        await ExhaustRetryChainAsync(fx, invoice, user, 3);

        var runner = BuildRunner(fx, gracePeriodDays: 3);
        var result = await runner.DetectCandidatesAsync(RunContext(nowUtc));

        result.SuspensionCandidates.Should().Be(0);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task GraceSuspensionRunner_InactiveNetworkAccount_NotPauseCandidate()
    {
        // Invoice qualifies but NA is Suspended → excluded by the
        // join filter (NetworkAccountStatus.Active only).
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, _) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-10),
            naStatus: NetworkAccountStatus.Suspended);
        await ExhaustRetryChainAsync(fx, invoice, user, 3);

        var runner = BuildRunner(fx, gracePeriodDays: 3);
        var result = await runner.DetectCandidatesAsync(RunContext(nowUtc));

        result.SuspensionCandidates.Should().Be(0);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task GraceSuspensionRunner_CancelledSchedule_NotPauseCandidate()
    {
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, _) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-10),
            scheduleStatus: ServiceBillingScheduleStatus.Cancelled);
        await ExhaustRetryChainAsync(fx, invoice, user, 3);

        var runner = BuildRunner(fx, gracePeriodDays: 3);
        var result = await runner.DetectCandidatesAsync(RunContext(nowUtc));

        result.SuspensionCandidates.Should().Be(0);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task GraceSuspensionRunner_RetriesNotExhausted_NotPauseCandidate()
    {
        // Only 2 failed retries with MaxRetryAttempts=3 → still in
        // retry window, must NOT be a candidate.
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, _) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-10));
        await ExhaustRetryChainAsync(fx, invoice, user, 2);

        var runner = BuildRunner(fx, gracePeriodDays: 3, maxRetryAttempts: 3);
        var result = await runner.DetectCandidatesAsync(RunContext(nowUtc));

        // Cheap filter picks it (grace expired) but per-item guard drops
        // it into SkippedRetriesNotExhausted.
        result.CandidatesSelected.Should().Be(1);
        result.SkippedRetriesNotExhausted.Should().Be(1);
        result.SuspensionCandidates.Should().Be(0);

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task GraceSuspensionRunner_PendingRetry_NotPauseCandidate()
    {
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, _) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-10));
        await ExhaustRetryChainAsync(fx, invoice, user, 3);
        // Add a fresh Pending retry — Stage 3 (retry runner) still owns
        // this invoice, grace stage must skip.
        TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice, user, 4,
            PaymentRetryAttemptStatus.Pending);
        await fx.DbContext.SaveChangesAsync();

        var runner = BuildRunner(fx, gracePeriodDays: 3);
        var result = await runner.DetectCandidatesAsync(RunContext(nowUtc));

        result.SkippedPendingRetry.Should().Be(1);
        result.SuspensionCandidates.Should().Be(0);

        await fx.DisposeAsync();
    }

    // ─── Master switch ──────────────────────────────────────────────

    [Fact]
    public async Task GraceSuspensionRunner_AutoBillingDisabled_SkipsAllProcessing()
    {
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, _) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-10));
        await ExhaustRetryChainAsync(fx, invoice, user, 3);

        var runner = BuildRunner(fx, gracePeriodDays: 3, enabled: false);
        var result = await runner.DetectCandidatesAsync(RunContext(nowUtc));

        result.CandidatesSelected.Should().Be(0);
        result.SuspensionCandidates.Should().Be(0);

        await fx.DisposeAsync();
    }

    // ─── Report-only invariant ──────────────────────────────────────

    [Fact]
    public async Task GraceSuspensionRunner_DoesNotMutateNetworkAccountStatus()
    {
        var nowUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var (fx, user, invoice, _, na) = await SeedInvoiceInGraceAsync(nowUtc.AddDays(-10));
        await ExhaustRetryChainAsync(fx, invoice, user, 3);

        var runner = BuildRunner(fx, gracePeriodDays: 3);
        await runner.DetectCandidatesAsync(RunContext(nowUtc));

        var loaded = await fx.DbContext.NetworkAccounts.AsNoTracking().SingleAsync(n => n.Id == na.Id);
        loaded.Status.Should().Be(NetworkAccountStatus.Active,
            "Phase 0E is report-only; suspension must NEVER mutate NetworkAccount.Status");
        loaded.SuspendedAtUtc.Should().BeNull();

        await fx.DisposeAsync();
    }
}

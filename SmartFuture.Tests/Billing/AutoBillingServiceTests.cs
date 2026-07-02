using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-7 focused tests for AutoBillingService.
//
// AutoBillingService.ChargeInvoiceAsync has 12 collaborators and 366
// lines but the vast majority of its complexity lives in the "should we
// even try to charge?" skip gates. These tests lock every one of those
// gates deterministically without touching any real provider — the
// charge resolver + applier mocks are strict on the skip paths so any
// leak would fail immediately.
//
// Also covers ReconcileProviderSettlementAsync (called by PayFast ITN
// after an async settlement) — the two branches (Success closes the
// attempt + cancels siblings; Failed marks + schedules a next retry).
public class AutoBillingServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        SqliteTestDbFixture Fixture,
        AutoBillingService Service,
        Mock<IRecurringChargeServiceResolver> ChargeResolver,
        Mock<IPaymentApplierService> Applier,
        Mock<IRecurringMandateSelector> MandateSelector);

    private static Harness Build(
        SqliteTestDbFixture fx,
        bool enabled = true,
        bool chargeAuthorizationEnabled = true,
        int maxRetryAttempts = 3)
    {
        var chargeResolver = new Mock<IRecurringChargeServiceResolver>(MockBehavior.Strict);
        var applier = new Mock<IPaymentApplierService>(MockBehavior.Strict);
        var mandateSelector = new Mock<IRecurringMandateSelector>(MockBehavior.Loose);
        mandateSelector.Setup(x => x.ResolveDefaultChargeableMandateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CustomerPaymentMandate?)null);

        var audit = new Mock<IAuditService>(MockBehavior.Loose);
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Loose);
        currentUser.SetupGet(x => x.UserId).Returns((Guid?)null);

        var env = new Mock<IHostEnvironment>(MockBehavior.Loose);
        env.SetupGet(x => x.EnvironmentName).Returns("Development");

        var notifications = new Mock<INotificationService>(MockBehavior.Loose);
        var email = new AutoBillingEmailService(
            notifications.Object,
            Options.Create(new AutoBillingSettings()),
            NullLogger<AutoBillingEmailService>.Instance);

        var svc = new AutoBillingService(
            fx.AppDbContext,
            chargeResolver.Object,
            applier.Object,
            audit.Object,
            currentUser.Object,
            Options.Create(new AutoBillingSettings
            {
                Enabled = enabled,
                ChargeAuthorizationEnabled = chargeAuthorizationEnabled,
                MaxRetryAttempts = maxRetryAttempts,
            }),
            Options.Create(new PaystackSettings()),
            Options.Create(new PayFastSettings()),
            mandateSelector.Object,
            env.Object,
            email,
            NullLogger<AutoBillingService>.Instance);

        return new Harness(fx, svc, chargeResolver, applier, mandateSelector);
    }

    private static async Task<(SmartFuture.Domain.Identity.User user, Invoice invoice)> SeedInvoiceAsync(
        SqliteTestDbFixture fx,
        InvoiceStatus invoiceStatus = InvoiceStatus.Issued,
        decimal balanceDue = 999m,
        bool autoBillingEnabled = true)
    {
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        TestEntityFactory.CreateCustomerProfile(fx.AppDbContext, user, autoBillingEnabled);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext, type: ServicePackageType.Fibre);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg);
        await fx.DbContext.SaveChangesAsync();
        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order, totalAmount: balanceDue,
            dueAtUtc: NowUtc.AddDays(-1), status: invoiceStatus);
        invoice.BalanceDue = balanceDue;
        await fx.DbContext.SaveChangesAsync();
        return (user, invoice);
    }

    // ─── Master gates ──────────────────────────────────────────────

    [Fact]
    public async Task ChargeInvoiceAsync_AutoBillingDisabled_SkipsWithoutTouchingProvider()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (_, invoice) = await SeedInvoiceAsync(fx);
        var h = Build(fx, enabled: false);

        var result = await h.Service.ChargeInvoiceAsync(invoice.Id, AutoBillingChargeSource.MonthlyRenewal);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        result.Data.FailureReason.Should().Contain("Enabled=false");
        h.ChargeResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChargeInvoiceAsync_ChargeAuthorizationDisabled_Skips()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (_, invoice) = await SeedInvoiceAsync(fx);
        var h = Build(fx, chargeAuthorizationEnabled: false);

        var result = await h.Service.ChargeInvoiceAsync(invoice.Id, AutoBillingChargeSource.MonthlyRenewal);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        result.Data.FailureReason.Should().Contain("ChargeAuthorizationEnabled=false");
        h.ChargeResolver.VerifyNoOtherCalls();
    }

    // ─── Invoice-not-found ─────────────────────────────────────────

    [Fact]
    public async Task ChargeInvoiceAsync_InvoiceNotFound_ReturnsFailure()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);

        var result = await h.Service.ChargeInvoiceAsync(Guid.NewGuid(), AutoBillingChargeSource.MonthlyRenewal);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be("Invoice not found.");
    }

    [Fact]
    public async Task ChargeInvoiceAsync_EmptyInvoiceId_ReturnsBadRequest()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);

        var result = await h.Service.ChargeInvoiceAsync(Guid.Empty, AutoBillingChargeSource.MonthlyRenewal);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("InvoiceId is required");
    }

    // ─── Invoice-in-terminal-state / zero-balance skips ───────────

    [Theory]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Cancelled)]
    [InlineData(InvoiceStatus.Void)]
    public async Task ChargeInvoiceAsync_InvoiceAlreadyClosed_Skips(InvoiceStatus status)
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (_, invoice) = await SeedInvoiceAsync(fx, invoiceStatus: status, balanceDue: 0m);
        var h = Build(fx);

        var result = await h.Service.ChargeInvoiceAsync(invoice.Id, AutoBillingChargeSource.MonthlyRenewal);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        h.ChargeResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChargeInvoiceAsync_ZeroBalance_Skips()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (_, invoice) = await SeedInvoiceAsync(fx, balanceDue: 0m);
        var h = Build(fx);

        var result = await h.Service.ChargeInvoiceAsync(invoice.Id, AutoBillingChargeSource.MonthlyRenewal);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        result.Data.FailureReason.Should().Contain("balance is zero");
    }

    // ─── Customer opt-in / mandate gates ──────────────────────────

    [Fact]
    public async Task ChargeInvoiceAsync_CustomerNotOptedIn_Skips()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (_, invoice) = await SeedInvoiceAsync(fx, autoBillingEnabled: false);
        var h = Build(fx);

        var result = await h.Service.ChargeInvoiceAsync(invoice.Id, AutoBillingChargeSource.MonthlyRenewal);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        result.Data.FailureReason.Should().Contain("opted");
        h.ChargeResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChargeInvoiceAsync_NoActiveMandate_Skips()
    {
        // Customer profile exists + opted in, but the mandate selector
        // returns null → skip.
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (_, invoice) = await SeedInvoiceAsync(fx);
        var h = Build(fx);
        // Default h.MandateSelector setup returns null.

        var result = await h.Service.ChargeInvoiceAsync(invoice.Id, AutoBillingChargeSource.MonthlyRenewal);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        result.Data.FailureReason.Should().Contain("mandate");
        h.ChargeResolver.VerifyNoOtherCalls();
    }

    // ─── Retry reuse-mode validation ──────────────────────────────

    [Fact]
    public async Task ChargeInvoiceAsync_RetryReuse_UnknownAttempt_Skips()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (user, invoice) = await SeedInvoiceAsync(fx);
        // Add a mandate so the mandate skip doesn't short-circuit us first.
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack);
        await fx.DbContext.SaveChangesAsync();
        var h = Build(fx);
        h.MandateSelector.Setup(x => x.ResolveDefaultChargeableMandateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomerPaymentMandate
            {
                Id = Guid.NewGuid(), UserId = user.Id, Provider = PaymentProviderType.Paystack,
                AuthorizationCodeProtected = "prot", IsActive = true, IsReusable = true, IsDefault = true,
            });

        var result = await h.Service.ChargeInvoiceAsync(
            invoice.Id, AutoBillingChargeSource.Retry, executeRetryAttemptId: Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        result.Data.FailureReason.Should().Contain("not found");
        h.ChargeResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChargeInvoiceAsync_RetryReuse_AttemptOnDifferentInvoice_Skips()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (user, invoice) = await SeedInvoiceAsync(fx);
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack);
        // A retry attempt bound to a DIFFERENT invoice.
        var otherInvoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, invoice.Order!, totalAmount: 500m);
        await fx.DbContext.SaveChangesAsync();
        var attempt = TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, otherInvoice, user, attemptNumber: 1,
            status: PaymentRetryAttemptStatus.Pending);
        await fx.DbContext.SaveChangesAsync();
        var h = Build(fx);
        h.MandateSelector.Setup(x => x.ResolveDefaultChargeableMandateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomerPaymentMandate
            {
                Id = Guid.NewGuid(), UserId = user.Id, Provider = PaymentProviderType.Paystack,
                AuthorizationCodeProtected = "prot", IsActive = true, IsReusable = true, IsDefault = true,
            });

        var result = await h.Service.ChargeInvoiceAsync(
            invoice.Id, AutoBillingChargeSource.Retry, executeRetryAttemptId: attempt.Id);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        result.Data.FailureReason.Should().Contain("does not belong to invoice");
    }

    [Fact]
    public async Task ChargeInvoiceAsync_RetryReuse_AttemptNotPending_Skips()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (user, invoice) = await SeedInvoiceAsync(fx);
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack);
        await fx.DbContext.SaveChangesAsync();
        var attempt = TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice, user, attemptNumber: 1,
            status: PaymentRetryAttemptStatus.Success);
        await fx.DbContext.SaveChangesAsync();
        var h = Build(fx);
        h.MandateSelector.Setup(x => x.ResolveDefaultChargeableMandateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomerPaymentMandate
            {
                Id = Guid.NewGuid(), UserId = user.Id, Provider = PaymentProviderType.Paystack,
                AuthorizationCodeProtected = "prot", IsActive = true, IsReusable = true, IsDefault = true,
            });

        var result = await h.Service.ChargeInvoiceAsync(
            invoice.Id, AutoBillingChargeSource.Retry, executeRetryAttemptId: attempt.Id);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        result.Data.FailureReason.Should().Contain("Pending");
    }

    [Fact]
    public async Task ChargeInvoiceAsync_RetryReuse_AttemptExceedsMax_Skips()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (user, invoice) = await SeedInvoiceAsync(fx);
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack);
        await fx.DbContext.SaveChangesAsync();
        var attempt = TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice, user, attemptNumber: 99,
            status: PaymentRetryAttemptStatus.Pending);
        await fx.DbContext.SaveChangesAsync();
        var h = Build(fx, maxRetryAttempts: 3);
        h.MandateSelector.Setup(x => x.ResolveDefaultChargeableMandateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomerPaymentMandate
            {
                Id = Guid.NewGuid(), UserId = user.Id, Provider = PaymentProviderType.Paystack,
                AuthorizationCodeProtected = "prot", IsActive = true, IsReusable = true, IsDefault = true,
            });

        var result = await h.Service.ChargeInvoiceAsync(
            invoice.Id, AutoBillingChargeSource.Retry, executeRetryAttemptId: attempt.Id);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Charged.Should().BeFalse();
        result.Data.FailureReason.Should().Contain("exceeds MaxRetryAttempts");
    }

    // ─── ReconcileProviderSettlementAsync ─────────────────────────

    [Fact]
    public async Task ReconcileProviderSettlementAsync_NoAutoBillingAttempt_IsNoOp()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        // No PaymentRetryAttempt seeded — the reconcile should quietly return.
        await h.Service.ReconcileProviderSettlementAsync(Guid.NewGuid(), completed: true);
        // Assertion: nothing threw. No verifiable side effects.
    }

    [Fact]
    public async Task ReconcileProviderSettlementAsync_EmptyPaymentId_IsNoOp()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var h = Build(fx);
        await h.Service.ReconcileProviderSettlementAsync(Guid.Empty, completed: true);
    }

    [Fact]
    public async Task ReconcileProviderSettlementAsync_Completed_MarksAttemptSuccess()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (user, invoice) = await SeedInvoiceAsync(fx);
        await fx.DbContext.SaveChangesAsync();
        var payment = new Payment
        {
            PaymentNumber = "PAY-1", InvoiceId = invoice.Id, Status = PaymentStatus.Pending,
            Amount = invoice.BalanceDue, CurrencyCode = "ZAR", GatewayName = "PayFast",
        };
        fx.DbContext.Payments.Add(payment);
        await fx.DbContext.SaveChangesAsync();
        var attempt = TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice, user, attemptNumber: 1,
            status: PaymentRetryAttemptStatus.Pending);
        attempt.PaymentId = payment.Id;
        await fx.DbContext.SaveChangesAsync();
        var h = Build(fx);

        await h.Service.ReconcileProviderSettlementAsync(payment.Id, completed: true);

        var reloaded = await fx.DbContext.PaymentRetryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        reloaded.Status.Should().Be(PaymentRetryAttemptStatus.Success);
    }

    [Fact]
    public async Task ReconcileProviderSettlementAsync_Completed_AttemptAlreadyTerminal_IsIdempotent()
    {
        // Duplicate ITN / RetryRunner self-heal already ran — the attempt
        // is already Success. Reconcile must NOT flip it back or mutate it.
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (user, invoice) = await SeedInvoiceAsync(fx);
        await fx.DbContext.SaveChangesAsync();
        var payment = new Payment
        {
            PaymentNumber = "PAY-1", InvoiceId = invoice.Id, Status = PaymentStatus.Completed,
            Amount = invoice.BalanceDue, CurrencyCode = "ZAR", GatewayName = "PayFast",
        };
        fx.DbContext.Payments.Add(payment);
        await fx.DbContext.SaveChangesAsync();
        var attempt = TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice, user, attemptNumber: 1,
            status: PaymentRetryAttemptStatus.Success);
        attempt.PaymentId = payment.Id;
        var originalUpdated = attempt.UpdatedAtUtc;
        await fx.DbContext.SaveChangesAsync();
        var h = Build(fx);

        await h.Service.ReconcileProviderSettlementAsync(payment.Id, completed: true);

        var reloaded = await fx.DbContext.PaymentRetryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        reloaded.Status.Should().Be(PaymentRetryAttemptStatus.Success,
            "already-terminal attempts must not be re-flipped — idempotent");
    }

    [Fact]
    public async Task ReconcileProviderSettlementAsync_Failed_MarksAttemptFailed()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var (user, invoice) = await SeedInvoiceAsync(fx);
        await fx.DbContext.SaveChangesAsync();
        var payment = new Payment
        {
            PaymentNumber = "PAY-1", InvoiceId = invoice.Id, Status = PaymentStatus.Pending,
            Amount = invoice.BalanceDue, CurrencyCode = "ZAR", GatewayName = "PayFast",
        };
        fx.DbContext.Payments.Add(payment);
        await fx.DbContext.SaveChangesAsync();
        var attempt = TestEntityFactory.CreateRetryAttempt(fx.AppDbContext, invoice, user, attemptNumber: 1,
            status: PaymentRetryAttemptStatus.Pending);
        attempt.PaymentId = payment.Id;
        await fx.DbContext.SaveChangesAsync();
        var h = Build(fx);

        await h.Service.ReconcileProviderSettlementAsync(payment.Id, completed: false);

        var reloaded = await fx.DbContext.PaymentRetryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        reloaded.Status.Should().Be(PaymentRetryAttemptStatus.Failed);
        reloaded.FailureReason.Should().Contain("Provider settlement reported failure");
    }
}

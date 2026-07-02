using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Application.ServiceChanges;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-4 fan-out verification for PaymentApplierService.ApplyStatusChangeAsync.
// Lock the "which post-commit hook fires when" contract:
//
//   • Service invoice paid → IServiceBillingScheduleService.Ensure... called.
//   • Any invoice paid → IServiceChangeRequestService.OnInvoicePaid called.
//   • Any invoice paid → IAuditService.Log called (paid + status transition).
//   • Failed / no-op status changes → billing schedule NOT called.
//
// Phase-3 already covered the arithmetic + persistence side. These
// tests assert the interfaces got the right calls at the right times.
public class PaymentApplierServiceFanOutTests
{
    private class Deps
    {
        public Mock<IAuditService> Audit = new(MockBehavior.Loose);
        public Mock<INotificationService> Notifications = new(MockBehavior.Loose);
        public Mock<INetworkAccountService> NetworkAccounts = new(MockBehavior.Loose);
        public Mock<IServiceChangeRequestService> ServiceChanges = new(MockBehavior.Loose);
        public Mock<IServiceBillingScheduleService> Schedules = new(MockBehavior.Loose);
    }

    private static PaymentApplierService BuildApplier(SqliteTestDbFixture fx, Guid actingUserId, Deps deps)
    {
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Loose);
        currentUser.SetupGet(x => x.UserId).Returns(actingUserId);
        var env = new Mock<IHostEnvironment>(MockBehavior.Loose);
        env.SetupGet(x => x.EnvironmentName).Returns("Development");

        return new PaymentApplierService(
            fx.AppDbContext,
            deps.Audit.Object,
            deps.Notifications.Object,
            deps.NetworkAccounts.Object,
            deps.ServiceChanges.Object,
            currentUser.Object,
            env.Object,
            Options.Create(new ServiceActivationSettings()),
            deps.Schedules.Object,
            NullLogger<PaymentApplierService>.Instance);
    }

    private static async Task<(SqliteTestDbFixture fx,
        SmartFuture.Domain.Identity.User user,
        SmartFuture.Domain.Orders.Order order,
        Invoice invoice,
        Payment payment)>
        SeedAsync(
            InvoiceLineItemType lineType = InvoiceLineItemType.ServicePackage,
            OrderStatus orderStatus = OrderStatus.AwaitingPayment)
    {
        var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext, type: ServicePackageType.Fibre);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg, status: orderStatus);
        await fx.DbContext.SaveChangesAsync();
        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order,
            totalAmount: 699m,
            dueAtUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            issuedAtUtc: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        TestEntityFactory.AddLineItem(fx.AppDbContext, invoice, lineType, 699m, "Test");
        await fx.DbContext.SaveChangesAsync();

        var payment = new Payment
        {
            PaymentNumber = $"PAY-{Guid.NewGuid():N}"[..15],
            InvoiceId = invoice.Id,
            Invoice = invoice,
            Status = PaymentStatus.Pending,
            Amount = 699m,
            CurrencyCode = "ZAR",
            GatewayName = "Paystack",
        };
        fx.DbContext.Payments.Add(payment);
        await fx.DbContext.SaveChangesAsync();

        return (fx, user, order, invoice, payment);
    }

    // ─── Success → all downstream hooks fire ───────────────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_ServiceInvoicePaid_CallsBillingScheduleEnsure()
    {
        var (fx, user, _, invoice, payment) = await SeedAsync(lineType: InvoiceLineItemType.ServicePackage);
        var deps = new Deps();
        var applier = BuildApplier(fx, user.Id, deps);

        var result = await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
            TriggerNotifications = true,
        });

        result.IsSuccess.Should().BeTrue(result.Message);
        deps.Schedules.Verify(x => x.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once,
            "service invoice becoming Paid must anchor the recurring schedule");

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ApplyStatusChangeAsync_InvoicePaid_CallsServiceChangeOnInvoicePaid()
    {
        var (fx, user, _, invoice, payment) = await SeedAsync();
        var deps = new Deps();
        var applier = BuildApplier(fx, user.Id, deps);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
            TriggerNotifications = true,
        });

        deps.ServiceChanges.Verify(x => x.OnInvoicePaidAsync(
            invoice.Id, payment.Id, It.IsAny<CancellationToken>()), Times.Once,
            "every invoice-paid transition must fire the service-change auto-complete hook");

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ApplyStatusChangeAsync_InvoicePaid_LogsAuditEntry()
    {
        var (fx, user, _, _, payment) = await SeedAsync();
        var deps = new Deps();
        var applier = BuildApplier(fx, user.Id, deps);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
        });

        deps.Audit.Verify(x => x.LogAsync(
            It.Is<CreateAuditLogRequestDto>(r => r.EntityType == SmartFuture.Shared.Enums.Auditing.AuditEntityType.Payment),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce,
            "payment status change must be audited");

        await fx.DisposeAsync();
    }

    // ─── Failure → NO service-flow hooks fire ──────────────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_Failure_DoesNotCallBillingScheduleEnsure()
    {
        var (fx, user, _, _, payment) = await SeedAsync();
        var deps = new Deps();
        var applier = BuildApplier(fx, user.Id, deps);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Failed,
            FailureReason = "insufficient_funds",
        });

        deps.Schedules.Verify(x => x.EnsureActivatedForPaidServiceInvoiceAsync(
            It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never,
            "a failed payment does not anchor a billing schedule");

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ApplyStatusChangeAsync_Failure_DoesNotCallServiceChangeHook()
    {
        var (fx, user, _, _, payment) = await SeedAsync();
        var deps = new Deps();
        var applier = BuildApplier(fx, user.Id, deps);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Failed,
        });

        deps.ServiceChanges.Verify(x => x.OnInvoicePaidAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        await fx.DisposeAsync();
    }

    // ─── Duplicate webhook → NO hooks fire ────────────────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_DuplicateWebhook_DoesNotDoubleFireHooks()
    {
        var (fx, user, _, invoice, payment) = await SeedAsync(lineType: InvoiceLineItemType.ServicePackage);
        var deps = new Deps();
        var applier = BuildApplier(fx, user.Id, deps);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
        });
        // Duplicate (Completed → Completed) — no-op.
        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
        });

        deps.Schedules.Verify(x => x.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once,
            "duplicate webhook must NOT re-anchor the schedule");
        deps.ServiceChanges.Verify(x => x.OnInvoicePaidAsync(
            invoice.Id, payment.Id, It.IsAny<CancellationToken>()), Times.Once,
            "duplicate webhook must NOT re-trigger the service-change hook");

        await fx.DisposeAsync();
    }

    // ─── Non-service invoice → schedule NOT anchored ───────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_NonServiceInvoicePaid_StillCallsBillingScheduleEnsure_ButScheduleDeclines()
    {
        // The applier always CALLS EnsureActivatedForPaidServiceInvoiceAsync
        // when an invoice becomes paid — the schedule service itself
        // internally filters non-service invoices out. That's the
        // documented separation of concerns.
        var (fx, user, _, invoice, payment) = await SeedAsync(lineType: InvoiceLineItemType.InstallationFee);
        var deps = new Deps();
        var applier = BuildApplier(fx, user.Id, deps);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
        });

        deps.Schedules.Verify(x => x.EnsureActivatedForPaidServiceInvoiceAsync(
            invoice.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once,
            "applier fires the hook for EVERY paid invoice; the schedule service filters internally");

        await fx.DisposeAsync();
    }
}

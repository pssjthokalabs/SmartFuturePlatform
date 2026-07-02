using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
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

// Phase-3 orchestrator-level tests for PaymentApplierService.ApplyStatusChangeAsync.
//
// Verifies the OUTCOMES that hit the DB after the pure PaymentSettlementCore
// decision + the transactional wrapper + the mutation step have all run.
// The 8 collaborator interfaces (audit / notifications / provisioning /
// service-change / current-user / host-env / activation-settings / billing-
// schedule) are Loose mocks — we don't care WHICH hooks fire in these
// tests, only that the DB rows land in the right shape. The individual
// signal fan-out is covered by PaymentSettlementCoreTests.
//
// SQLite EF provider registers a non-retrying execution strategy, so the
// `strategy.ExecuteAsync` + `BeginTransactionAsync` chain in the applier
// works identically to production behaviour in test.
public class PaymentApplierServiceOrchestratorTests
{
    // The Payment / Order LastStatusChangedByUser FK is Restrict on
    // delete but still enforced on write. If the "acting admin" GUID
    // isn't a real Users row, SaveChanges fails with SQLite error 19
    // (FOREIGN KEY constraint failed). Every test that constructs the
    // applier seeds an admin User FIRST and hands its Id to
    // ICurrentUserService.
    private static PaymentApplierService BuildApplier(
        SqliteTestDbFixture fx,
        Guid actingUserId,
        bool isProduction = false,
        bool requireManualOpenserveActivation = true)
    {
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Loose);
        currentUser.SetupGet(x => x.UserId).Returns(actingUserId);
        currentUser.SetupGet(x => x.IpAddress).Returns((string?)null);
        currentUser.SetupGet(x => x.UserAgent).Returns((string?)null);

        var env = new Mock<IHostEnvironment>(MockBehavior.Loose);
        env.SetupGet(x => x.EnvironmentName).Returns(isProduction ? "Production" : "Development");

        return new PaymentApplierService(
            fx.AppDbContext,
            new Mock<IAuditService>(MockBehavior.Loose).Object,
            new Mock<INotificationService>(MockBehavior.Loose).Object,
            new Mock<INetworkAccountService>(MockBehavior.Loose).Object,
            new Mock<IServiceChangeRequestService>(MockBehavior.Loose).Object,
            currentUser.Object,
            env.Object,
            Options.Create(new ServiceActivationSettings
                { RequireManualOpenserveActivation = requireManualOpenserveActivation }),
            new Mock<IServiceBillingScheduleService>(MockBehavior.Loose).Object,
            NullLogger<PaymentApplierService>.Instance);
    }

    private static async Task<(
        SqliteTestDbFixture fx,
        SmartFuture.Domain.Identity.User user,
        SmartFuture.Domain.Orders.Order order,
        Invoice invoice,
        Payment payment)>
        SeedPendingPaymentAsync(
            decimal invoiceTotal = 999m,
            OrderStatus orderStatus = OrderStatus.AwaitingPayment,
            InvoiceStatus invoiceStatus = InvoiceStatus.Issued,
            InvoiceLineItemType lineType = InvoiceLineItemType.InstallationFee)
    {
        var fx = await SqliteTestDbFixture.CreateAsync();

        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext, type: ServicePackageType.Fibre);
        var order = TestEntityFactory.CreateOrder(fx.AppDbContext, user, pkg, status: orderStatus);
        await fx.DbContext.SaveChangesAsync();

        var invoice = TestEntityFactory.CreateInvoice(fx.AppDbContext, order,
            totalAmount: invoiceTotal,
            dueAtUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            issuedAtUtc: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            status: invoiceStatus);
        TestEntityFactory.AddLineItem(fx.AppDbContext, invoice, lineType, invoiceTotal, "Test");
        await fx.DbContext.SaveChangesAsync();

        var payment = new Payment
        {
            PaymentNumber = $"PAY-TEST-{Guid.NewGuid():N}"[..15],
            InvoiceId = invoice.Id,
            Invoice = invoice,
            Status = PaymentStatus.Pending,
            Method = PaymentMethodType.Card,
            Amount = invoiceTotal,
            CurrencyCode = "ZAR",
            GatewayName = "Paystack",
        };
        fx.DbContext.Payments.Add(payment);
        await fx.DbContext.SaveChangesAsync();

        return (fx, user, order, invoice, payment);
    }

    // ─── Success path ──────────────────────────────────────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_Success_PersistsInvoicePaid()
    {
        var (fx, user, _, invoice, payment) = await SeedPendingPaymentAsync(invoiceTotal: 999m);
        var applier = BuildApplier(fx, user.Id);

        var result = await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
            GatewayReference = "GW-REF-1",
            GatewayTransactionId = "GW-TX-1",
        });

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Data.Should().NotBeNull();

        var reloadedInvoice = await fx.DbContext.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        reloadedInvoice.Status.Should().Be(InvoiceStatus.Paid);
        reloadedInvoice.AmountPaid.Should().Be(999m);
        reloadedInvoice.BalanceDue.Should().Be(0m);
        reloadedInvoice.PaidAtUtc.Should().NotBeNull();

        var reloadedPayment = await fx.DbContext.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        reloadedPayment.Status.Should().Be(PaymentStatus.Completed);
        reloadedPayment.PaidAtUtc.Should().NotBeNull();
        reloadedPayment.GatewayReference.Should().Be("GW-REF-1");
        reloadedPayment.GatewayTransactionId.Should().Be("GW-TX-1");

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ApplyStatusChangeAsync_Success_TransitionsAwaitingPaymentOrderToPaymentReceived()
    {
        var (fx, user, order, _, payment) = await SeedPendingPaymentAsync(
            invoiceTotal: 999m, orderStatus: OrderStatus.AwaitingPayment);
        var applier = BuildApplier(fx, user.Id);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
        });

        var reloaded = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        reloaded.Status.Should().Be(OrderStatus.PaymentReceived);

        await fx.DisposeAsync();
    }

    // ─── Idempotency ─────────────────────────────────────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_DuplicateWebhook_DoesNotDoubleApplyPayment()
    {
        // First webhook: Pending → Completed. Second webhook (replay):
        // Completed → Completed. Post-second-run, the invoice must
        // still show AmountPaid=999 (not 1998) and BalanceDue=0.
        var (fx, user, _, invoice, payment) = await SeedPendingPaymentAsync(invoiceTotal: 999m);
        var applier = BuildApplier(fx, user.Id);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
            GatewayReference = "GW-REF-1",
        });

        // Second, duplicate webhook.
        var replay = await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
            GatewayReference = "GW-REF-1-REPLAY",   // updated gateway ref survives
        });

        replay.IsSuccess.Should().BeTrue();

        var reloadedInvoice = await fx.DbContext.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        reloadedInvoice.Status.Should().Be(InvoiceStatus.Paid);
        reloadedInvoice.AmountPaid.Should().Be(999m,
            "duplicate webhook must not re-apply arithmetic — AmountPaid stays at 999, not 1998");
        reloadedInvoice.BalanceDue.Should().Be(0m);

        var reloadedPayment = await fx.DbContext.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        reloadedPayment.GatewayReference.Should().Be("GW-REF-1-REPLAY",
            "gateway ref update from the duplicate webhook must land");

        await fx.DisposeAsync();
    }

    // ─── Failure path ────────────────────────────────────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_Failure_DoesNotPersistInvoicePaid()
    {
        var (fx, user, _, invoice, payment) = await SeedPendingPaymentAsync(invoiceTotal: 999m);
        var applier = BuildApplier(fx, user.Id);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Failed,
            FailureReason = "Insufficient funds",
        });

        var reloadedInvoice = await fx.DbContext.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        reloadedInvoice.Status.Should().Be(InvoiceStatus.Issued,
            "failed payment must leave the invoice unpaid");
        reloadedInvoice.AmountPaid.Should().Be(0m);
        reloadedInvoice.BalanceDue.Should().Be(999m);
        reloadedInvoice.PaidAtUtc.Should().BeNull();

        var reloadedPayment = await fx.DbContext.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        reloadedPayment.Status.Should().Be(PaymentStatus.Failed);
        reloadedPayment.FailedAtUtc.Should().NotBeNull();
        reloadedPayment.FailureReason.Should().Be("Insufficient funds");

        await fx.DisposeAsync();
    }

    // ─── Refund path ─────────────────────────────────────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_Refund_ReversesInvoicePaid()
    {
        // Sequence: Pending → Completed → Refunded. Final state must
        // show invoice reverted to Issued, AmountPaid back to 0,
        // BalanceDue back to Total.
        var (fx, user, _, invoice, payment) = await SeedPendingPaymentAsync(invoiceTotal: 999m);
        var applier = BuildApplier(fx, user.Id);

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Completed,
        });

        await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = payment.Id,
            NewStatus = PaymentStatus.Refunded,
        });

        var reloadedInvoice = await fx.DbContext.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        reloadedInvoice.Status.Should().Be(InvoiceStatus.Issued);
        reloadedInvoice.AmountPaid.Should().Be(0m);
        reloadedInvoice.BalanceDue.Should().Be(999m);
        reloadedInvoice.PaidAtUtc.Should().BeNull();

        var reloadedPayment = await fx.DbContext.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        reloadedPayment.Status.Should().Be(PaymentStatus.Refunded);
        reloadedPayment.RefundedAtUtc.Should().NotBeNull();

        await fx.DisposeAsync();
    }

    // ─── Not-found path ──────────────────────────────────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_UnknownPayment_ReturnsNotFound()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var admin = TestEntityFactory.CreateUser(fx.AppDbContext);
        await fx.DbContext.SaveChangesAsync();
        var applier = BuildApplier(fx, admin.Id);

        var result = await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = Guid.NewGuid(),
            NewStatus = PaymentStatus.Completed,
        });

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().NotBeEmpty();
    }

    // ─── Validation ──────────────────────────────────────────────

    [Fact]
    public async Task ApplyStatusChangeAsync_EmptyPaymentId_ReturnsValidationError()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var admin = TestEntityFactory.CreateUser(fx.AppDbContext);
        await fx.DbContext.SaveChangesAsync();
        var applier = BuildApplier(fx, admin.Id);

        var result = await applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = Guid.Empty,
            NewStatus = PaymentStatus.Completed,
        });

        result.IsSuccess.Should().BeFalse();
    }
}

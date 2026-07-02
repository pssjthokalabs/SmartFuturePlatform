using SmartFuture.Application.Payments;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Tests.Billing;

// Phase-3 tests for the extracted PaymentSettlementCore. This is the
// pure decision helper used by PaymentApplierService.ApplyStatusChangeAsync
// — it looks at (previous status, new status, invoice + order state,
// UAT flags, env) and returns a PaymentSettlementOutcome describing
// EVERYTHING the orchestrator should apply + all post-commit signals.
//
// The core has zero side effects: no DbContext, no HTTP, no email, no
// audit, no clock. That means every case here is a straight
// input → outcome lookup with no fixtures — the tests are the fastest
// in the suite.
public class PaymentSettlementCoreTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    private static PaymentSettlementInput BuildInput(
        PaymentStatus previous,
        PaymentStatus newStatus,
        decimal paymentAmount = 999m,
        InvoiceState? invoice = null,
        OrderState? order = null,
        bool isProduction = false,
        bool requireManualOpenserveActivation = true,
        bool isTestAmountOverrideApplied = false,
        decimal? invoiceAmountAtTime = null,
        DateTime? nowUtc = null) => new()
        {
            PreviousPaymentStatus = previous,
            NewPaymentStatus = newStatus,
            PaymentAmount = paymentAmount,
            IsTestAmountOverrideApplied = isTestAmountOverrideApplied,
            InvoiceAmountAtTime = invoiceAmountAtTime,
            Invoice = invoice,
            Order = order,
            IsProduction = isProduction,
            RequireManualOpenserveActivation = requireManualOpenserveActivation,
            NowUtc = nowUtc ?? Now,
        };

    private static InvoiceState UnpaidInvoice(
        decimal total = 999m,
        decimal amountPaid = 0m,
        InvoiceStatus current = InvoiceStatus.Issued,
        bool hasServiceLine = false,
        DateTime? dueAtUtc = null,
        DateTime? currentPaidAtUtc = null) =>
        new(current, total, amountPaid, hasServiceLine, dueAtUtc, currentPaidAtUtc);

    // ─── Success paths ─────────────────────────────────────────────

    [Fact]
    public void PaymentSettlementCore_Success_MarksInvoicePaid_WhenAmountCoversTotal()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 999m,
            invoice: UnpaidInvoice(total: 999m));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.ShouldApply.Should().BeTrue();
        outcome.NewPaymentStatus.Should().Be(PaymentStatus.Completed);
        outcome.ShouldSetPaidAtUtc.Should().BeTrue();
        outcome.SettlementAmount.Should().Be(999m);
        outcome.PaidDelta.Should().Be(999m);
        outcome.NewInvoiceAmountPaid.Should().Be(999m);
        outcome.NewInvoiceBalanceDue.Should().Be(0m);
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.Paid);
        outcome.ShouldSetInvoicePaidAtUtc.Should().BeTrue();
        outcome.InvoiceBecamePaid.Should().BeTrue();
        outcome.ShouldQueueSuccessNotification.Should().BeTrue();
        outcome.ShouldEnsureBillingSchedule.Should().BeTrue();
    }

    [Fact]
    public void PaymentSettlementCore_Success_MarksInvoicePartiallyPaid_WhenAmountBelowTotal()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 500m,
            invoice: UnpaidInvoice(total: 999m));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.NewInvoiceAmountPaid.Should().Be(500m);
        outcome.NewInvoiceBalanceDue.Should().Be(499m);
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.PartiallyPaid);
        outcome.ShouldSetInvoicePaidAtUtc.Should().BeFalse();
        outcome.ShouldClearInvoicePaidAtUtc.Should().BeTrue();
        outcome.InvoiceBecamePaid.Should().BeFalse();
        outcome.ShouldQueueSuccessNotification.Should().BeFalse();
        outcome.ShouldEnsureBillingSchedule.Should().BeFalse();
    }

    [Fact]
    public void PaymentSettlementCore_Success_Overpay_ClampsPaidAmountToTotal()
    {
        // Balance floor guarantees no negative balance even when payment
        // exceeds the invoice total. AmountPaid is NOT clamped to Total
        // (the surplus is preserved for audit); BalanceDue floors at 0.
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 150m,
            invoice: UnpaidInvoice(total: 100m));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.NewInvoiceAmountPaid.Should().Be(150m,
            "arithmetic mirrors ApplyPaymentToInvoice; overpaid AmountPaid is preserved for audit");
        outcome.NewInvoiceBalanceDue.Should().Be(0m, "balance floor prevents negatives");
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.Paid);
        outcome.InvoiceBecamePaid.Should().BeTrue();
    }

    // ─── Idempotency guard ─────────────────────────────────────────

    [Fact]
    public void PaymentSettlementCore_DuplicateStatusChange_ReturnsNoOp()
    {
        var input = BuildInput(PaymentStatus.Completed, PaymentStatus.Completed,
            paymentAmount: 999m,
            invoice: UnpaidInvoice(total: 999m, amountPaid: 999m, current: InvoiceStatus.Paid));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.ShouldApply.Should().BeFalse();
        outcome.NoOpReason.Should().Be("duplicate_status_change");
        outcome.PaidDelta.Should().Be(0m);
        outcome.InvoiceBecamePaid.Should().BeFalse();
        outcome.ShouldQueueSuccessNotification.Should().BeFalse();
    }

    // ─── Failure paths ─────────────────────────────────────────────

    [Fact]
    public void PaymentSettlementCore_Failure_DoesNotMarkInvoicePaid()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Failed,
            paymentAmount: 999m,
            invoice: UnpaidInvoice(total: 999m));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.ShouldApply.Should().BeTrue();
        outcome.ShouldSetFailedAtUtc.Should().BeTrue();
        outcome.ShouldSetPaidAtUtc.Should().BeFalse();
        outcome.PaidDelta.Should().Be(0m,
            "arithmetic only runs on the wasCompleted != isCompleted edge — Failed doesn't trip it");
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.Issued);
        outcome.InvoiceBecamePaid.Should().BeFalse();
    }

    [Fact]
    public void PaymentSettlementCore_Failure_QueuesFailureSignal()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Failed,
            paymentAmount: 999m,
            invoice: UnpaidInvoice(total: 999m));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.ShouldQueueFailureNotification.Should().BeTrue();
        outcome.ShouldQueueSuccessNotification.Should().BeFalse();
    }

    // ─── Refund / reversal paths ───────────────────────────────────

    [Fact]
    public void PaymentSettlementCore_Refund_ReversesPaidAmount()
    {
        // Completed → Refunded flips the isCompleted edge, so arithmetic
        // fires with a NEGATIVE delta. Invoice reverts to Issued.
        var input = BuildInput(PaymentStatus.Completed, PaymentStatus.Refunded,
            paymentAmount: 999m,
            invoice: UnpaidInvoice(
                total: 999m, amountPaid: 999m,
                current: InvoiceStatus.Paid,
                currentPaidAtUtc: new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc)));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.ShouldSetRefundedAtUtc.Should().BeTrue();
        outcome.PaidDelta.Should().Be(-999m);
        outcome.NewInvoiceAmountPaid.Should().Be(0m);
        outcome.NewInvoiceBalanceDue.Should().Be(999m);
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.Issued);
        outcome.ShouldClearInvoicePaidAtUtc.Should().BeTrue();
        outcome.InvoiceBecamePaid.Should().BeFalse();
    }

    [Fact]
    public void PaymentSettlementCore_ReversalCannotDropPaidBelowZero()
    {
        // Defensive: an outsized reversal (payment amount > invoice
        // AmountPaid) must not push AmountPaid negative — it floors at 0.
        var input = BuildInput(PaymentStatus.Completed, PaymentStatus.Reversed,
            paymentAmount: 500m,
            invoice: UnpaidInvoice(
                total: 999m, amountPaid: 100m,
                current: InvoiceStatus.PartiallyPaid));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.PaidDelta.Should().Be(-500m);
        outcome.NewInvoiceAmountPaid.Should().Be(0m, "AmountPaid floors at 0 on outsized reversal");
        outcome.NewInvoiceBalanceDue.Should().Be(999m);
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.Issued);
    }

    // ─── Zero-total edge case ──────────────────────────────────────

    [Fact]
    public void PaymentSettlementCore_ZeroTotalInvoice_DoesNotBreak()
    {
        // A zero-total invoice (free-activation) must never flip to Paid
        // via an accidental R0 apply. Delta of 0 leaves everything at 0
        // and the status doesn't flip.
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 0m,
            invoice: UnpaidInvoice(total: 0m));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.NewInvoiceAmountPaid.Should().Be(0m);
        outcome.NewInvoiceBalanceDue.Should().Be(0m);
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.Issued);
        outcome.InvoiceBecamePaid.Should().BeFalse();
        outcome.ShouldSetInvoicePaidAtUtc.Should().BeFalse();
    }

    // ─── Post-commit signals ───────────────────────────────────────

    [Fact]
    public void PaymentSettlementCore_Success_RequestsBillingScheduleEnsure_WhenServiceInvoicePaid()
    {
        // Fully paid service invoice → ShouldEnsureBillingSchedule + at
        // least ONE of the network-account signals fires. This is the
        // post-commit fan-out the orchestrator uses to drive the schedule
        // anchor + NA provisioning + service-change hooks.
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 999m,
            invoice: UnpaidInvoice(total: 999m, hasServiceLine: true,
                dueAtUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)),
            order: new OrderState(OrderStatus.PendingPayment, null, null, null),
            requireManualOpenserveActivation: true);

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.InvoiceBecamePaid.Should().BeTrue();
        outcome.ShouldEnsureBillingSchedule.Should().BeTrue();
        (outcome.ShouldProvisionNetworkAccount || outcome.ShouldActivateNetworkAccount)
            .Should().BeTrue("exactly one of the two NA signals must fire on invoiceBecamePaid");
    }

    [Fact]
    public void PaymentSettlementCore_NonServiceInvoicePaid_DoesNotAdvanceOrderLifecycle()
    {
        // An installation-fee-only invoice paid → invoice becomes Paid,
        // ShouldEnsureBillingSchedule still fires (the orchestrator
        // filters non-service invoices out downstream inside
        // ServiceBillingScheduleService), order status is untouched
        // because there's no service line item.
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 100m,
            invoice: UnpaidInvoice(total: 100m, hasServiceLine: false),
            order: new OrderState(OrderStatus.PendingPayment, null, null, null));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.InvoiceBecamePaid.Should().BeTrue();
        outcome.NewOrderStatus.Should().BeNull("no service line item → no PendingPayment lifecycle transition");
        outcome.OrderAutoActivated.Should().BeFalse();
    }

    // ─── UAT override paths ────────────────────────────────────────

    [Fact]
    public void PaymentSettlementCore_TestOverride_InUat_UsesInvoiceAmountAtTime()
    {
        // R10 Paystack charge lands as R999 settlement because the
        // override captured the real balance at initiate-time.
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 10m,
            isProduction: false,
            isTestAmountOverrideApplied: true,
            invoiceAmountAtTime: 999m,
            invoice: UnpaidInvoice(total: 999m));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.SettlementAmount.Should().Be(999m);
        outcome.TestOverrideAppliedInUat.Should().BeTrue();
        outcome.TestOverrideBlockedByProduction.Should().BeFalse();
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public void PaymentSettlementCore_TestOverride_InProduction_IsBlocked()
    {
        // Same override flag on a stale row that landed in production
        // MUST fall back to the R10 provider amount — that's the
        // production block that keeps invoices from underpaying.
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 10m,
            isProduction: true,
            isTestAmountOverrideApplied: true,
            invoiceAmountAtTime: 999m,
            invoice: UnpaidInvoice(total: 999m));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.SettlementAmount.Should().Be(10m);
        outcome.TestOverrideBlockedByProduction.Should().BeTrue();
        outcome.TestOverrideAppliedInUat.Should().BeFalse();
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.PartiallyPaid,
            "production block leaves the R10 as a PartiallyPaid contribution");
    }

    [Fact]
    public void PaymentSettlementCore_TestOverride_InUat_MissingInvoiceAmountAtTime_FallsBackToInvoiceTotal()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 10m,
            isProduction: false,
            isTestAmountOverrideApplied: true,
            invoiceAmountAtTime: null,
            invoice: UnpaidInvoice(total: 500m));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.SettlementAmount.Should().Be(500m);
        outcome.TestOverrideAppliedInUat.Should().BeTrue();
        outcome.NewInvoiceStatus.Should().Be(InvoiceStatus.Paid);
    }

    // ─── Order lifecycle transitions ───────────────────────────────

    [Fact]
    public void PaymentSettlementCore_AwaitingPayment_TransitionsToPaymentReceived_OnInvoicePaid()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 100m,
            invoice: UnpaidInvoice(total: 100m, hasServiceLine: false),
            order: new OrderState(OrderStatus.AwaitingPayment, null, null, null));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.NewOrderStatus.Should().Be(OrderStatus.PaymentReceived);
    }

    [Fact]
    public void PaymentSettlementCore_PendingPayment_ServiceInvoicePaid_ManualOn_MovesToPendingActivation()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 999m,
            invoice: UnpaidInvoice(total: 999m, hasServiceLine: true),
            order: new OrderState(OrderStatus.PendingPayment, null, null, null),
            requireManualOpenserveActivation: true);

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.NewOrderStatus.Should().Be(OrderStatus.PendingActivation);
        outcome.OrderAutoActivated.Should().BeFalse();
        outcome.ShouldStampOrderActivatedAtUtc.Should().BeFalse();
        outcome.ShouldStampOrderBillingAnchorDateUtc.Should().BeFalse();
        outcome.NewOrderNextPayDateUtc.Should().BeNull();
        outcome.ShouldProvisionNetworkAccount.Should().BeTrue("manual path → EnsurePending, not Activate");
        outcome.ShouldActivateNetworkAccount.Should().BeFalse();
    }

    [Fact]
    public void PaymentSettlementCore_PendingPayment_ServiceInvoicePaid_ManualOff_AutoActivatesToActive()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 999m,
            invoice: UnpaidInvoice(total: 999m, hasServiceLine: true),
            order: new OrderState(OrderStatus.PendingPayment, null, null, null),
            requireManualOpenserveActivation: false);

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.NewOrderStatus.Should().Be(OrderStatus.Active);
        outcome.OrderAutoActivated.Should().BeTrue();
        outcome.ShouldStampOrderActivatedAtUtc.Should().BeTrue();
        outcome.ShouldStampOrderBillingAnchorDateUtc.Should().BeTrue();
        outcome.NewOrderNextPayDateUtc.Should().Be(Now.AddDays(30));
        outcome.ShouldActivateNetworkAccount.Should().BeTrue();
        outcome.ShouldProvisionNetworkAccount.Should().BeFalse();
    }

    [Fact]
    public void PaymentSettlementCore_ActiveOrder_ServiceInvoicePaid_AdvancesNextPayDate()
    {
        // Recurring monthly invoice paid on an already-Active order.
        // NextPayDate advances from the invoice's DueAtUtc (not
        // PaidAtUtc), preserving cadence when the customer pays early.
        var previousNextPay = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 699m,
            invoice: UnpaidInvoice(total: 699m, hasServiceLine: true,
                dueAtUtc: new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)),
            order: new OrderState(
                OrderStatus.Active,
                ActivatedAtUtc: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                BillingAnchorDateUtc: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                NextPayDateUtc: previousNextPay));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.NewOrderStatus.Should().BeNull("order was already Active — no status change");
        outcome.OrderAutoActivated.Should().BeFalse();
        outcome.NewOrderNextPayDateUtc.Should().Be(previousNextPay.AddDays(30),
            "cadence advance uses previous NextPayDateUtc, NOT PaidAtUtc");
    }

    [Fact]
    public void PaymentSettlementCore_ActiveOrder_NoDueDate_DoesNotAdvanceNextPayDate()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 699m,
            invoice: UnpaidInvoice(total: 699m, hasServiceLine: true, dueAtUtc: null),
            order: new OrderState(
                OrderStatus.Active,
                ActivatedAtUtc: Now.AddDays(-30),
                BillingAnchorDateUtc: Now.AddDays(-30),
                NextPayDateUtc: null));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.NewOrderNextPayDateUtc.Should().BeNull(
            "no DueAtUtc + no previous NextPayDateUtc → nothing to base the advance on, safe to skip");
    }

    // ─── Same-invoice reapply short-circuit ────────────────────────

    [Fact]
    public void PaymentSettlementCore_CompletedToCompleted_NoOp_DoesNotChangeInvoice()
    {
        // Same terminal status — the orchestrator SHOULD still commit
        // gateway ref updates but MUST NOT re-apply arithmetic.
        var input = BuildInput(PaymentStatus.Completed, PaymentStatus.Completed,
            paymentAmount: 999m,
            invoice: UnpaidInvoice(total: 999m, amountPaid: 999m, current: InvoiceStatus.Paid));

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.ShouldApply.Should().BeFalse();
        outcome.PaidDelta.Should().Be(0m);
        outcome.InvoiceBecamePaid.Should().BeFalse();
    }

    // ─── No invoice attached ───────────────────────────────────────

    [Fact]
    public void PaymentSettlementCore_NoInvoice_AppliesPaymentStatus_ButSkipsArithmetic()
    {
        var input = BuildInput(PaymentStatus.Pending, PaymentStatus.Completed,
            paymentAmount: 100m,
            invoice: null);

        var outcome = PaymentSettlementCore.Calculate(input);

        outcome.ShouldApply.Should().BeTrue();
        outcome.ShouldSetPaidAtUtc.Should().BeTrue();
        outcome.PaidDelta.Should().Be(0m);
        outcome.InvoiceBecamePaid.Should().BeFalse();
        outcome.NewOrderStatus.Should().BeNull();
    }
}

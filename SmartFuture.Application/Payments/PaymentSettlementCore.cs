using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Application.Payments;

/// <summary>
/// Pure, side-effect-free decision core for a single Payment status
/// transition. Encapsulates the "given the pre-change state + the
/// requested new status, what should each of Payment / Invoice / Order
/// end up as, and what post-commit signals should fire?" reasoning that
/// used to live inline in <see cref="PaymentApplierService.ApplyStatusChangeAsync"/>.
///
/// Contract:
///   • No DbContext access, no HTTP, no email, no audit, no provider
///     calls, no <c>DateTime.UtcNow</c>. Everything the caller needs to
///     make decisions is on <see cref="PaymentSettlementInput"/>.
///   • Every field of <see cref="PaymentSettlementOutcome"/> is a
///     value the orchestrator applies to tracked entities OR a signal
///     the post-commit hooks act on. Nothing is decided later.
///   • Duplicates (previous == new status) short-circuit with
///     <c>ShouldApply=false</c> so the orchestrator can skip the
///     arithmetic entirely.
///
/// The arithmetic + status-transition rules exactly mirror the previous
/// inline implementation, including the UAT test-amount override + the
/// production block. That was verified by:
///   1. Running the existing PaymentApplierMathTests (locking
///      <see cref="PaymentApplierService.ApplyPaymentToInvoice"/>).
///   2. Adding <see cref="PaymentSettlementCoreTests"/> for the
///      end-to-end decision shape.
/// </summary>
public static class PaymentSettlementCore
{
    /// <summary>
    /// Compute the settlement outcome for a Payment status transition.
    /// Pure — no side effects, safe to call from tests without any DI.
    /// </summary>
    public static PaymentSettlementOutcome Calculate(PaymentSettlementInput input)
    {
        // ─── 1. Idempotency guard ─────────────────────────────────
        // Previous == new means the webhook / admin call is a replay
        // OR a same-status "refresh gateway references" update. Skip
        // all arithmetic; the orchestrator still gets the chance to
        // persist gateway ref updates and commit.
        if (input.PreviousPaymentStatus == input.NewPaymentStatus)
        {
            return PaymentSettlementOutcome.NoOp(
                previousStatus: input.PreviousPaymentStatus,
                reason: "duplicate_status_change");
        }

        // ─── 2. Payment timestamp flags ───────────────────────────
        var shouldSetPaidAt =
            input.NewPaymentStatus == PaymentStatus.Completed;
        var shouldSetFailedAt =
            input.NewPaymentStatus == PaymentStatus.Failed;
        var shouldSetRefundedAt =
            input.NewPaymentStatus is PaymentStatus.Refunded or PaymentStatus.Reversed;

        var wasCompleted = input.PreviousPaymentStatus == PaymentStatus.Completed;
        var isCompleted = input.NewPaymentStatus == PaymentStatus.Completed;
        var completedEdgeChanged = wasCompleted != isCompleted;

        // ─── 3. No invoice / no completion-edge change → skip arithmetic ─
        // We still record the payment mutation itself and emit the
        // status-transition timestamp signals — but there is nothing to
        // apply to an invoice or order.
        if (input.Invoice is null || !completedEdgeChanged)
        {
            return new PaymentSettlementOutcome
            {
                ShouldApply = true,
                NoOpReason = string.Empty,
                NewPaymentStatus = input.NewPaymentStatus,
                ShouldSetPaidAtUtc = shouldSetPaidAt,
                ShouldSetFailedAtUtc = shouldSetFailedAt,
                ShouldSetRefundedAtUtc = shouldSetRefundedAt,
                SettlementAmount = 0m,
                PaidDelta = 0m,
                TestOverrideBlockedByProduction = false,
                TestOverrideAppliedInUat = false,
                NewInvoiceAmountPaid = input.Invoice?.CurrentAmountPaid ?? 0m,
                NewInvoiceBalanceDue = input.Invoice is null
                    ? 0m
                    : Math.Max(0m, input.Invoice.Value.TotalAmount - input.Invoice.Value.CurrentAmountPaid),
                NewInvoiceStatus = input.Invoice?.CurrentStatus ?? InvoiceStatus.Draft,
                ShouldSetInvoicePaidAtUtc = false,
                ShouldClearInvoicePaidAtUtc = false,
                InvoiceBecamePaid = false,
                NewOrderStatus = null,
                ShouldStampOrderActivatedAtUtc = false,
                ShouldStampOrderBillingAnchorDateUtc = false,
                NewOrderNextPayDateUtc = null,
                OrderAutoActivated = false,
                ShouldQueueSuccessNotification = false,
                ShouldQueueFailureNotification = shouldSetFailedAt,
                ShouldEnsureBillingSchedule = false,
                ShouldProvisionNetworkAccount = false,
                ShouldActivateNetworkAccount = false,
            };
        }

        // ─── 4. Resolve settlement amount ─────────────────────────
        // Handle the UAT test-amount override. Production ALWAYS blocks
        // the override, even if a stale row from a UAT restore is
        // present — the invoice will underpay by the delta between the
        // R10 test amount and the real balance, and the orchestrator's
        // structured error log records the block.
        var invoice = input.Invoice.Value;
        var settlementAmount = input.PaymentAmount;
        var testOverrideBlockedByProduction = false;
        var testOverrideAppliedInUat = false;
        if (input.IsTestAmountOverrideApplied)
        {
            if (input.IsProduction)
            {
                testOverrideBlockedByProduction = true;
                // settlementAmount stays at input.PaymentAmount — production block.
            }
            else if (input.InvoiceAmountAtTime is > 0m)
            {
                settlementAmount = input.InvoiceAmountAtTime.Value;
                testOverrideAppliedInUat = true;
            }
            else if (invoice.TotalAmount > 0m)
            {
                // Fallback path when InvoiceAmountAtTime wasn't
                // captured at initiate time (legacy UAT rows).
                settlementAmount = invoice.TotalAmount;
                testOverrideAppliedInUat = true;
            }
        }

        // ─── 5. Delta + invoice arithmetic ────────────────────────
        var delta = isCompleted ? settlementAmount : -settlementAmount;
        var (newPaid, newBalance, newInvoiceStatus, shouldSetInvoicePaidAt, shouldClearInvoicePaidAt) =
            ComputeInvoiceArithmetic(invoice, delta);

        var invoiceBecamePaid = newInvoiceStatus == InvoiceStatus.Paid
                             && invoice.CurrentStatus != InvoiceStatus.Paid;

        // ─── 6. Order status transitions ──────────────────────────
        var (newOrderStatus, orderAutoActivated, shouldStampActivatedAt,
             shouldStampBillingAnchor, newNextPayDate) =
            ComputeOrderTransitions(
                orderState: input.Order,
                hasServiceLineItem: invoice.HasServiceLineItem,
                invoiceStatusIsPaid: newInvoiceStatus == InvoiceStatus.Paid,
                invoiceBecamePaid: invoiceBecamePaid,
                invoiceDueAtUtc: invoice.DueAtUtc,
                requireManualOpenserveActivation: input.RequireManualOpenserveActivation,
                nowUtc: input.NowUtc);

        // ─── 7. Post-commit signals ──────────────────────────────
        return new PaymentSettlementOutcome
        {
            ShouldApply = true,
            NoOpReason = string.Empty,
            NewPaymentStatus = input.NewPaymentStatus,
            ShouldSetPaidAtUtc = shouldSetPaidAt,
            ShouldSetFailedAtUtc = shouldSetFailedAt,
            ShouldSetRefundedAtUtc = shouldSetRefundedAt,
            SettlementAmount = settlementAmount,
            PaidDelta = delta,
            TestOverrideBlockedByProduction = testOverrideBlockedByProduction,
            TestOverrideAppliedInUat = testOverrideAppliedInUat,
            NewInvoiceAmountPaid = newPaid,
            NewInvoiceBalanceDue = newBalance,
            NewInvoiceStatus = newInvoiceStatus,
            ShouldSetInvoicePaidAtUtc = shouldSetInvoicePaidAt,
            ShouldClearInvoicePaidAtUtc = shouldClearInvoicePaidAt,
            InvoiceBecamePaid = invoiceBecamePaid,
            NewOrderStatus = newOrderStatus,
            ShouldStampOrderActivatedAtUtc = shouldStampActivatedAt,
            ShouldStampOrderBillingAnchorDateUtc = shouldStampBillingAnchor,
            NewOrderNextPayDateUtc = newNextPayDate,
            OrderAutoActivated = orderAutoActivated,
            ShouldQueueSuccessNotification = invoiceBecamePaid,
            ShouldQueueFailureNotification = shouldSetFailedAt,
            ShouldEnsureBillingSchedule = invoiceBecamePaid,
            ShouldProvisionNetworkAccount = invoiceBecamePaid && !orderAutoActivated,
            ShouldActivateNetworkAccount = invoiceBecamePaid && orderAutoActivated,
        };
    }

    // Pure arithmetic — mirrors PaymentApplierService.ApplyPaymentToInvoice
    // but returns the new values instead of mutating so the outcome
    // record is a snapshot the orchestrator applies later. If either
    // implementation changes, the PaymentApplierMathTests + the tests
    // for ApplyPaymentToInvoice under Phase 2 will flag the drift.
    private static (
        decimal NewAmountPaid,
        decimal NewBalanceDue,
        InvoiceStatus NewStatus,
        bool ShouldSetPaidAtUtc,
        bool ShouldClearPaidAtUtc)
        ComputeInvoiceArithmetic(InvoiceState invoice, decimal delta)
    {
        var newPaid = invoice.CurrentAmountPaid + delta;
        if (newPaid < 0m) newPaid = 0m;

        var newBalance = invoice.TotalAmount - newPaid;
        if (newBalance < 0m) newBalance = 0m;

        InvoiceStatus newStatus;
        bool shouldSetPaidAt;
        bool shouldClearPaidAt;

        if (newPaid >= invoice.TotalAmount && invoice.TotalAmount > 0m)
        {
            newStatus = InvoiceStatus.Paid;
            shouldSetPaidAt = invoice.CurrentPaidAtUtc is null;
            shouldClearPaidAt = false;
        }
        else if (newPaid > 0m)
        {
            newStatus = InvoiceStatus.PartiallyPaid;
            shouldSetPaidAt = false;
            shouldClearPaidAt = true;
        }
        else
        {
            // Reverting to Issued only when we WERE Paid or PartiallyPaid.
            // Otherwise the previous status stands (e.g. Draft stays Draft).
            if (invoice.CurrentStatus is InvoiceStatus.Paid or InvoiceStatus.PartiallyPaid)
            {
                newStatus = InvoiceStatus.Issued;
                shouldSetPaidAt = false;
                shouldClearPaidAt = true;
            }
            else
            {
                newStatus = invoice.CurrentStatus;
                shouldSetPaidAt = false;
                shouldClearPaidAt = false;
            }
        }

        return (newPaid, newBalance, newStatus, shouldSetPaidAt, shouldClearPaidAt);
    }

    // Applies the go-live order lifecycle rules that used to live in
    // ApplyStatusChangeAsync lines 243–326. Returns the target order
    // status (null = unchanged), the OrderAutoActivated flag (true when
    // we transitioned PendingPayment → Active), the two stamp flags
    // (ActivatedAtUtc + BillingAnchorDateUtc), and the new
    // NextPayDateUtc (null = unchanged).
    private static (
        OrderStatus? NewOrderStatus,
        bool OrderAutoActivated,
        bool ShouldStampActivatedAtUtc,
        bool ShouldStampBillingAnchorDateUtc,
        DateTime? NewNextPayDateUtc)
        ComputeOrderTransitions(
            OrderState? orderState,
            bool hasServiceLineItem,
            bool invoiceStatusIsPaid,
            bool invoiceBecamePaid,
            DateTime? invoiceDueAtUtc,
            bool requireManualOpenserveActivation,
            DateTime nowUtc)
    {
        if (orderState is null) return (null, false, false, false, null);
        var order = orderState.Value;

        // Case A: AwaitingPayment → PaymentReceived. Fires whenever the
        // invoice is Paid after arithmetic and the order was still
        // AwaitingPayment. Applies to ALL invoices, not just service
        // lines (installation-fee, one-off, etc.).
        OrderStatus? newOrderStatus = null;
        var orderAutoActivated = false;
        var shouldStampActivatedAt = false;
        var shouldStampBillingAnchor = false;
        DateTime? newNextPayDate = null;

        if (invoiceStatusIsPaid && order.CurrentStatus == OrderStatus.AwaitingPayment)
        {
            newOrderStatus = OrderStatus.PaymentReceived;
        }

        // Case B + C fire only for service-line invoices (ServicePackage
        // or ProRata) that JUST became Paid — the go-live activation
        // rule + the recurring-cadence anchor advance.
        if (!invoiceBecamePaid || !hasServiceLineItem) return
            (newOrderStatus, orderAutoActivated, shouldStampActivatedAt, shouldStampBillingAnchor, newNextPayDate);

        if (order.CurrentStatus == OrderStatus.PendingPayment)
        {
            if (requireManualOpenserveActivation)
            {
                newOrderStatus = OrderStatus.PendingActivation;
            }
            else
            {
                newOrderStatus = OrderStatus.Active;
                orderAutoActivated = true;
                shouldStampActivatedAt = order.ActivatedAtUtc is null;
                shouldStampBillingAnchor = order.BillingAnchorDateUtc is null;
                if (order.NextPayDateUtc is null) newNextPayDate = nowUtc.AddDays(30);
            }
        }
        else if (order.CurrentStatus == OrderStatus.Active && invoiceDueAtUtc.HasValue)
        {
            // Already Active — recurring monthly invoice paid. Advance
            // NextPayDateUtc from the previous NextPayDateUtc OR the
            // invoice's DueAtUtc, never from PaidAtUtc. That preserves
            // the cadence when a customer pays early.
            var basis = order.NextPayDateUtc ?? invoiceDueAtUtc.Value;
            newNextPayDate = basis.AddDays(30);
        }

        return (newOrderStatus, orderAutoActivated, shouldStampActivatedAt, shouldStampBillingAnchor, newNextPayDate);
    }
}

/// <summary>
/// Everything <see cref="PaymentSettlementCore.Calculate"/> needs to
/// make a decision. Values are captured from tracked entities +
/// environment BEFORE any mutation, so tests can rebuild the input
/// state precisely without a DbContext.
/// </summary>
public sealed record PaymentSettlementInput
{
    public required PaymentStatus PreviousPaymentStatus { get; init; }
    public required PaymentStatus NewPaymentStatus { get; init; }
    public required decimal PaymentAmount { get; init; }
    public bool IsTestAmountOverrideApplied { get; init; }
    public decimal? InvoiceAmountAtTime { get; init; }

    /// <summary>Null when the Payment has no linked Invoice (rare — admin standalone).</summary>
    public InvoiceState? Invoice { get; init; }

    /// <summary>Null when the Invoice has no Order (foreign, admin-issued, etc.).</summary>
    public OrderState? Order { get; init; }

    /// <summary>
    /// True when <c>IHostEnvironment.IsProduction()</c>. Used to block
    /// the UAT test-amount override at settlement time even if a stale
    /// row from a UAT restore made it into a production DB.
    /// </summary>
    public required bool IsProduction { get; init; }

    /// <summary>
    /// From <c>ServiceActivationSettings.RequireManualOpenserveActivation</c>.
    /// True (production-safe default) means a PendingPayment order
    /// moves to PendingActivation on first-invoice-paid, waiting for an
    /// admin to run <c>AdminActivateServiceAsync</c>. False (UAT / no
    /// carrier step) means the order jumps straight to Active.
    /// </summary>
    public required bool RequireManualOpenserveActivation { get; init; }

    /// <summary>Clock — passed in so the core stays pure.</summary>
    public required DateTime NowUtc { get; init; }
}

/// <summary>Snapshot of the tracked Invoice's state before mutation.</summary>
public readonly record struct InvoiceState(
    InvoiceStatus CurrentStatus,
    decimal TotalAmount,
    decimal CurrentAmountPaid,
    bool HasServiceLineItem,
    DateTime? DueAtUtc,
    DateTime? CurrentPaidAtUtc);

/// <summary>Snapshot of the tracked Order's state before mutation.</summary>
public readonly record struct OrderState(
    OrderStatus CurrentStatus,
    DateTime? ActivatedAtUtc,
    DateTime? BillingAnchorDateUtc,
    DateTime? NextPayDateUtc);

/// <summary>
/// The complete decision produced by <see cref="PaymentSettlementCore.Calculate"/>.
/// The orchestrator applies these values to tracked entities + emits
/// the post-commit signals accordingly.
/// </summary>
public sealed record PaymentSettlementOutcome
{
    /// <summary>False when the transition is a no-op (duplicate status change).</summary>
    public bool ShouldApply { get; init; }

    /// <summary>Documentation for a no-op — one of the well-known strings below.</summary>
    public string NoOpReason { get; init; } = string.Empty;

    // ─── Payment mutation signals ──────────────────────────────
    public PaymentStatus NewPaymentStatus { get; init; }
    public bool ShouldSetPaidAtUtc { get; init; }
    public bool ShouldSetFailedAtUtc { get; init; }
    public bool ShouldSetRefundedAtUtc { get; init; }

    // ─── Settlement math ──────────────────────────────────────
    /// <summary>Amount actually applied to the invoice (with UAT override resolution).</summary>
    public decimal SettlementAmount { get; init; }

    /// <summary>Signed delta added to Invoice.AmountPaid (negative on reversal).</summary>
    public decimal PaidDelta { get; init; }

    /// <summary>True when the UAT override was carried but production blocked it.</summary>
    public bool TestOverrideBlockedByProduction { get; init; }

    /// <summary>True when the UAT override was applied — the invoice will settle full.</summary>
    public bool TestOverrideAppliedInUat { get; init; }

    // ─── Invoice mutation signals ──────────────────────────────
    public decimal NewInvoiceAmountPaid { get; init; }
    public decimal NewInvoiceBalanceDue { get; init; }
    public InvoiceStatus NewInvoiceStatus { get; init; }
    public bool ShouldSetInvoicePaidAtUtc { get; init; }
    public bool ShouldClearInvoicePaidAtUtc { get; init; }
    public bool InvoiceBecamePaid { get; init; }

    // ─── Order mutation signals ──────────────────────────────
    /// <summary>Null when the Order status is unchanged.</summary>
    public OrderStatus? NewOrderStatus { get; init; }
    public bool ShouldStampOrderActivatedAtUtc { get; init; }
    public bool ShouldStampOrderBillingAnchorDateUtc { get; init; }

    /// <summary>Null when NextPayDateUtc is unchanged; set on auto-activate + on recurring-cycle advance.</summary>
    public DateTime? NewOrderNextPayDateUtc { get; init; }
    public bool OrderAutoActivated { get; init; }

    // ─── Post-commit signals ─────────────────────────────────
    public bool ShouldQueueSuccessNotification { get; init; }
    public bool ShouldQueueFailureNotification { get; init; }
    public bool ShouldEnsureBillingSchedule { get; init; }
    public bool ShouldProvisionNetworkAccount { get; init; }
    public bool ShouldActivateNetworkAccount { get; init; }

    /// <summary>Well-known no-op factory: previous == new status.</summary>
    public static PaymentSettlementOutcome NoOp(PaymentStatus previousStatus, string reason) => new()
    {
        ShouldApply = false,
        NoOpReason = reason,
        NewPaymentStatus = previousStatus,
        NewInvoiceStatus = InvoiceStatus.Draft,
    };
}

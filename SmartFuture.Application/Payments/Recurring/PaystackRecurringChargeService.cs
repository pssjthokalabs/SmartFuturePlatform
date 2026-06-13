using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0A — Paystack implementation of <see cref="IRecurringChargeService"/>.
/// A thin adapter over the existing <see cref="PaystackChargeAuthorizationService"/>;
/// it adds NO new behaviour and issues NO charge on its own. The mapping
/// below is deliberately byte-equivalent to the inline result-handling
/// that previously lived in <c>AutoBillingService.ChargeInvoiceAsync</c>:
///
///   • precondition failure (Result not success) → Failed, raw message, no tx id
///   • provider-rejected     (outcome.Success == false) → Failed, message
///       "Paystack returned status '…'." fallback, tx id if present
///   • settled               (outcome.Success == true)  → Success, tx id + paidAt
///
/// Paystack settles synchronously, so this adapter never returns
/// <see cref="RecurringChargeOutcomeKind.Pending"/>.
/// </summary>
public sealed class PaystackRecurringChargeService : IRecurringChargeService
{
    private readonly PaystackChargeAuthorizationService _inner;

    public PaystackRecurringChargeService(PaystackChargeAuthorizationService inner)
    {
        _inner = inner;
    }

    public PaymentProviderType Provider => PaymentProviderType.Paystack;

    public async Task<RecurringChargeResult> ChargeAsync(
        CustomerPaymentMandate mandate,
        decimal amountZar,
        string reference,
        RecurringChargeContext context,
        CancellationToken cancellationToken = default)
    {
        var chargeResult = await _inner.ChargeAsync(mandate.Id, amountZar, reference, cancellationToken);

        // Precondition failure (gates flipped, mandate unreadable, transport
        // error, etc.) — preserve the RAW message (may be null), no tx id.
        if (!chargeResult.IsSuccess)
            return RecurringChargeResult.Failed(chargeResult.Message, providerTransactionId: null);

        var outcome = chargeResult.Data!;
        if (!outcome.Success)
            return RecurringChargeResult.Failed(
                outcome.GatewayMessage ?? $"Paystack returned status '{outcome.Status}'.",
                providerTransactionId: outcome.ProviderTransactionId,
                providerStatus: outcome.Status);

        return RecurringChargeResult.Success(
            providerTransactionId: outcome.ProviderTransactionId,
            paidAtUtc: outcome.PaidAtUtc,
            providerStatus: outcome.Status);
    }
}

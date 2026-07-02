using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.PayFast;

/// <summary>
/// Pure, side-effect-free mapper for PayFast ITN payloads. Extracted
/// from <see cref="PayFastNotifyHandler"/> so the "raw provider status →
/// canonical <see cref="PaymentStatus"/>" decision is unit-testable
/// without loading the full 9-dependency handler.
///
/// Signature verification stays in <see cref="PayFastSignatureCalculator"/>
/// (already pure, already testable). This mapper only covers the status
/// translation + amount comparison rules the handler applies BEFORE it
/// calls <c>IPaymentApplierService.ApplyStatusChangeAsync</c>.
/// </summary>
public static class PayFastItnMapper
{
    /// <summary>
    /// Map a PayFast <c>payment_status</c> string to the SmartFuture
    /// <see cref="PaymentStatus"/> we hand to the applier.
    ///
    /// Mapping mirrors <c>PayFastNotifyHandler.MapPayFastStatus</c>:
    ///   • "COMPLETE" → <see cref="PaymentStatus.Completed"/>
    ///   • "FAILED"   → <see cref="PaymentStatus.Failed"/>
    ///   • "PENDING"  → null (no state change; handler ignores)
    ///   • unknown / null / empty → null
    /// </summary>
    public static PaymentStatus? MapPaymentStatus(string? payFastPaymentStatus)
    {
        if (string.IsNullOrWhiteSpace(payFastPaymentStatus)) return null;
        return payFastPaymentStatus.Trim().ToUpperInvariant() switch
        {
            "COMPLETE" => PaymentStatus.Completed,
            "FAILED" => PaymentStatus.Failed,
            "PENDING" => null,
            _ => null
        };
    }

    /// <summary>
    /// Compare an ITN's <c>amount_gross</c> to the expected invoice /
    /// initiation amount within PayFast's documented 1-cent rounding
    /// tolerance. Returns true when the values agree.
    ///
    /// PayFast documents that <c>amount_gross</c> is settled to 2
    /// decimal places (ZAR cents). We allow a 0.01 tolerance so a
    /// harmless representation drift (e.g. 349.77 vs 349.77) never
    /// rejects a legitimate ITN, but a real mismatch (349.77 vs 100.00)
    /// still fails.
    /// </summary>
    public static bool AmountsMatch(decimal expectedZar, decimal reportedAmountGross, decimal toleranceZar = 0.01m)
    {
        if (toleranceZar < 0m) toleranceZar = 0m;
        return Math.Abs(expectedZar - reportedAmountGross) <= toleranceZar;
    }

    /// <summary>
    /// Same-merchant guard. The ITN reports the merchant_id the
    /// payment landed on; the handler compares it to
    /// <c>PayFastSettings.MerchantId</c>. A mismatch is a
    /// hard-reject — the ITN came from a different account.
    /// </summary>
    public static bool MerchantMatches(string? configuredMerchantId, string? reportedMerchantId)
    {
        if (string.IsNullOrWhiteSpace(configuredMerchantId)) return false;
        if (string.IsNullOrWhiteSpace(reportedMerchantId)) return false;
        return string.Equals(configuredMerchantId.Trim(), reportedMerchantId.Trim(), StringComparison.Ordinal);
    }
}

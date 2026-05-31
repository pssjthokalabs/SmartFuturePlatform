using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Domain.Billing;

public class Payment : BaseEntity
{
    public string PaymentNumber { get; set; } = string.Empty;

    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public PaymentMethodType Method { get; set; } = PaymentMethodType.Other;

    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "ZAR";

    public DateTime? PaidAtUtc { get; set; }
    public DateTime? FailedAtUtc { get; set; }
    public DateTime? RefundedAtUtc { get; set; }

    public string? GatewayName { get; set; }
    public string? GatewayReference { get; set; }
    public string? GatewayTransactionId { get; set; }
    public string? ExternalReference { get; set; }
    public string? FailureReason { get; set; }
    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public User? LastStatusChangedByUser { get; set; }

    // ─── UAT live test-amount override audit ────────────────────────
    //
    // Set when the Paystack/AutoBilling __UseTestAmountOverride flags
    // routed the charge through a small fixed amount (e.g. R10) on a
    // non-production environment. These fields preserve the original
    // intent — what we wanted to charge vs. what we actually charged —
    // so the apply path can settle the full invoice and audit shows
    // exactly what happened.
    //
    // Production NEVER sets IsTestAmountOverrideApplied=true. The apply
    // path additionally refuses to honour the override when env is
    // Production, defending against a stale row from a UAT restore.

    /// <summary>True when this Payment was sent to the gateway with the test-amount override applied.</summary>
    public bool IsTestAmountOverrideApplied { get; set; } = false;

    /// <summary>Actual amount sent to the gateway (= <see cref="Amount"/> on override rows). Null for normal payments.</summary>
    public decimal? ActualProviderAmount { get; set; }

    /// <summary>Invoice balance at the moment of charge. Used by the override-aware apply path to settle the full invoice.</summary>
    public decimal? InvoiceAmountAtTime { get; set; }

    /// <summary>Free-text reason for the override (e.g. "UAT Paystack live R10 override").</summary>
    public string? TestOverrideReason { get; set; }
}

namespace SmartFuture.Application.Payments.PayFast;

/// <summary>
/// Bridges the generic webhook route
/// <c>POST /api/webhooks/payments/payfast</c> to the PayFast-specific
/// notify handler.
///
/// CRITICAL CONTEXT — why this exists:
/// The generic <see cref="Webhooks.IWebhookInboxService"/> is built for
/// JSON-bodied webhooks with a per-provider
/// <see cref="Infrastructure.Webhooks.IProviderSignatureValidator"/>. PayFast
/// ITNs are <c>application/x-www-form-urlencoded</c> and use MD5+passphrase
/// signing. With no PayFast signature validator registered the generic
/// inbox rejected every ITN with <c>WebhookInboxStatus.SignatureInvalid</c>,
/// the OrderIntent stayed Pending, and money taken via the live PayFast
/// merchant never materialised into an Order.
///
/// This bridge:
/// 1. Parses the raw form body itself (no JSON dependency).
/// 2. Persists a <c>WebhookInbox</c> audit row so /admin/inbox still shows
///    the receipt.
/// 3. Dispatches to <see cref="PayFastNotifyHandler"/> which does its own
///    signature + merchant-id + amount validation and materialises the
///    Order via <c>ConvertIntentPaymentToPaidOrderAsync</c>.
/// 4. Updates the audit row with the outcome (Processed, Failed, etc.).
///
/// Idempotency, signature validation, and amount validation all live in
/// <see cref="PayFastNotifyHandler"/> — this class is purely a transport
/// adapter + audit recorder.
/// </summary>
public interface IPayFastWebhookBridge
{
    /// <param name="preParsedFields">
    /// Optional. When the controller has already parsed the form
    /// (via <c>Request.ReadFormAsync()</c>), pass the form dictionary
    /// here so the bridge does not re-parse — Request.Body is a
    /// forward-only non-seekable stream and reading it after the form
    /// reader already consumed it yields an empty payload (the
    /// SHA-256 of "" — <c>E3B0C44…</c> — was the smoking gun in the
    /// 2026-06 UAT incident). When null, the bridge falls back to
    /// parsing <paramref name="rawFormBody"/> itself.
    /// </param>
    /// <param name="postedFieldsOrdered">
    /// Same fields as <paramref name="preParsedFields"/> but with the
    /// posted ORDER preserved. Required for ITN signature
    /// verification: PayFast computes its MD5 over the fields in the
    /// order it sent them, including empty fields, excluding only
    /// <c>signature</c>. A dictionary is not enough because key
    /// ordering is not part of the dictionary contract; the calculator
    /// must iterate the posting order.
    /// </param>
    Task<PayFastWebhookBridgeOutcome> HandleAsync(
        string rawFormBody,
        IReadOnlyDictionary<string, string>? preParsedFields,
        IReadOnlyList<KeyValuePair<string, string>>? postedFieldsOrdered,
        string? signatureHeader,
        string? providerEventIdHeader,
        string? idempotencyKeyHeader,
        CancellationToken cancellationToken = default);
}

public class PayFastWebhookBridgeOutcome
{
    public bool Accepted { get; set; }
    public string Message { get; set; } = string.Empty;
    /// <summary>Inbox row id — useful for cross-referencing /admin/inbox
    /// from PayFast's webhook delivery log.</summary>
    public Guid InboxId { get; set; }
    /// <summary>The provider event id the bridge synthesised for this
    /// ITN — either the stable tuple
    /// "payfast:{m_payment_id}:{pf_payment_id}:{payment_status}" for
    /// well-formed payloads, or the malformed-unique form for empty/
    /// partial payloads. Exposed so the UAT forensic capture can write
    /// it into the per-request JSON file.</summary>
    public string? ProviderEventId { get; set; }

    /// <summary>The computed (expected) MD5 signature the API derived
    /// from the posted form fields. Same value the handler compared
    /// against <see cref="PostedSignature"/>.</summary>
    public string? ComputedSignature { get; set; }
    /// <summary>The signature value as PayFast posted it (field
    /// <c>signature</c>).</summary>
    public string? PostedSignature { get; set; }
    /// <summary>Field order, redacted base string, and chosen
    /// algorithm. Safe — passphrase is masked. Forensic capture writes
    /// this to disk.</summary>
    public PayFastItnSignatureDebug? SignatureDebug { get; set; }
}

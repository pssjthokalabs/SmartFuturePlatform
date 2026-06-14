using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.PayFast;

public class PayFastNotifyHandler
{
    private readonly IAppDbContext _dbContext;
    private readonly IPaymentApplierService _applier;
    private readonly IOrderIntentService _orderIntentService;
    private readonly ICustomerPaymentMandateService _mandates;
    private readonly IAutoBillingService _autoBilling;
    private readonly PayFastSettings _settings;
    private readonly ILogger<PayFastNotifyHandler> _logger;

    public PayFastNotifyHandler(
        IAppDbContext dbContext,
        IPaymentApplierService applier,
        IOrderIntentService orderIntentService,
        ICustomerPaymentMandateService mandates,
        IAutoBillingService autoBilling,
        IOptions<PayFastSettings> settings,
        ILogger<PayFastNotifyHandler> logger)
    {
        _dbContext = dbContext;
        _applier = applier;
        _orderIntentService = orderIntentService;
        _mandates = mandates;
        _autoBilling = autoBilling;
        _settings = settings.Value;
        _logger = logger;
    }

    public Task<PayFastNotifyOutcome> HandleAsync(PayFastNotifyPayload payload, CancellationToken cancellationToken)
        => HandleAsync(payload, postedFields: null, cancellationToken);

    /// <summary>
    /// Overload that accepts the original ordered form fields PayFast
    /// posted. When supplied, signature validation uses the
    /// ITN-correct algorithm in
    /// <see cref="PayFastSignatureCalculator.GenerateItnSignature"/>:
    /// posted-order, include empty fields, exclude only "signature".
    ///
    /// When <paramref name="postedFields"/> is null, falls back to the
    /// legacy typed-payload signature derivation (used by the direct
    /// <c>/api/payments/payfast/notify</c> route which still binds via
    /// <c>[FromForm]</c> only).
    /// </summary>
    public async Task<PayFastNotifyOutcome> HandleAsync(
        PayFastNotifyPayload payload,
        IReadOnlyList<KeyValuePair<string, string>>? postedFields,
        CancellationToken cancellationToken)
    {
        if (payload is null) return new PayFastNotifyOutcome(false, "Empty payload");

        if (!_settings.IsConfigured)
        {
            _logger.LogWarning("Received PayFast ITN but PayFast is not configured — ignoring.");
            return new PayFastNotifyOutcome(false, "PayFast not configured");
        }

        _logger.LogInformation(
            "[PayFastNotifyDebug] Received ITN: m_payment_id={Reference} pf_payment_id={PfPaymentId} " +
            "payment_status={Status} amount_gross={Amount} merchant_id={MerchantId}",
            payload.MPaymentId, payload.PfPaymentId, payload.PaymentStatus,
            payload.AmountGross, payload.MerchantId);

        // 1. Validate merchant_id
        if (!string.Equals(payload.MerchantId, _settings.MerchantId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("PayFast ITN merchant_id mismatch: expected={Expected} got={Got}",
                _settings.MerchantId, payload.MerchantId);
            var mismatchOutcome = new PayFastNotifyOutcome(false, "Merchant ID mismatch");
            return mismatchOutcome;
        }

        // 2. Validate signature
        //
        // Prefer the ITN-correct algorithm when the bridge passed us
        // the original posted-field order. PayFast iterates EVERY
        // POSTed field except `signature`, including empties like
        // `item_description=` and `custom_str1=`, AND includes
        // negative values verbatim (e.g. `amount_fee=-2.70`). The
        // legacy GenerateSignature() path skipped empties and used
        // `value > 0` on amount_fee — both broke real ITNs.
        string expectedSignature;
        PayFastItnSignatureDebug? signatureDebug = null;
        if (postedFields is not null && postedFields.Count > 0)
        {
            expectedSignature = PayFastSignatureCalculator.GenerateItnSignature(
                postedFields, _settings.Passphrase, out signatureDebug);
        }
        else
        {
            // Legacy callers (PaymentsController.PayFastNotify with
            // [FromForm]) — fall back to the typed-payload signature
            // and warn so the issue is visible in logs.
            _logger.LogWarning(
                "[PayFastNotifyDebug] Signature validation falling back to legacy typed-payload algorithm for reference {Reference}. Caller should supply the ordered posted fields.",
                payload.MPaymentId);
            var signatureParams = BuildSignatureParams(payload);
            expectedSignature = PayFastSignatureCalculator.GenerateSignature(signatureParams, _settings.Passphrase);
        }

        var match = PayFastSignatureCalculator.SignaturesMatch(expectedSignature, payload.Signature);
        _logger.LogInformation(
            "[payment][payfast][signature_check] reference={Reference} match={Match} algorithm={Algorithm} fieldCount={FieldCount} passphraseConfigured={PassphraseConfigured}",
            payload.MPaymentId, match,
            signatureDebug?.Algorithm ?? "legacy-typed-payload",
            signatureDebug?.FieldNamesInOrder.Count ?? -1,
            signatureDebug?.PassphraseConfigured ?? !string.IsNullOrWhiteSpace(_settings.Passphrase));

        // Local helper — attaches signature debug to every outcome the
        // method returns from here on. Safe to call always: the debug
        // struct is the redacted variant (passphrase is masked).
        PayFastNotifyOutcome WithSig(PayFastNotifyOutcome o)
        {
            o.SignatureDebug = signatureDebug;
            o.PostedSignature = payload.Signature;
            o.ComputedSignature = expectedSignature;
            return o;
        }

        if (!match)
        {
            _logger.LogWarning(
                "[PayFastNotifyDebug] Signature mismatch — reference={Reference} posted={Posted} computed={Computed} algorithm={Algorithm} fieldOrder={FieldOrder}",
                payload.MPaymentId, payload.Signature, expectedSignature,
                signatureDebug?.Algorithm ?? "legacy-typed-payload",
                signatureDebug is null ? "(n/a)" : string.Join(",", signatureDebug.FieldNamesInOrder));
            return WithSig(new PayFastNotifyOutcome(false, "Signature mismatch"));
        }

        if (string.IsNullOrWhiteSpace(payload.MPaymentId))
            return WithSig(new PayFastNotifyOutcome(false, "Missing m_payment_id"));

        // 3a. Intent-first lookup. New-order PayFast payments mint an
        //     OrderIntent (no Invoice/Payment until paid) so there's
        //     no PaymentInitiation row yet — the intent itself stores
        //     the m_payment_id. When the intent matches AND PayFast
        //     reports COMPLETE, hand off to the conversion service to
        //     materialise Order + Invoice + Payment atomically. The
        //     conversion service calls IPaymentApplierService itself,
        //     so we don't need to apply status again afterwards.
        var intent = await _dbContext.OrderIntents
            .FirstOrDefaultAsync(o => o.Provider == PaymentProviderType.PayFast
                                   && o.IntentPaymentReference == payload.MPaymentId,
                                 cancellationToken);
        if (intent is not null)
        {
            _logger.LogInformation(
                "[PayFastNotifyDebug] Intent match — reference={Reference} intentId={IntentId} status={IntentStatus} paystackStatus={PaystackStatus}",
                payload.MPaymentId, intent.Id, intent.Status, payload.PaymentStatus);

            var intentMapped = MapPayFastStatus(payload.PaymentStatus);

            // Validate amount BEFORE materialising the order — even
            // though the conversion service re-reads the intent, we
            // do not want to convert an intent whose ITN payload has
            // a mismatched amount.
            var expectedAmount = intent.IntentPaymentAmount ?? intent.IntentInvoiceAmountAtTime ?? 0m;
            if (expectedAmount > 0m && Math.Abs(expectedAmount - payload.AmountGross) > 0.01m)
            {
                _logger.LogWarning(
                    "[PayFastNotifyDebug] Intent amount mismatch for {Reference}: expected {Expected}, got {Got}",
                    payload.MPaymentId, expectedAmount, payload.AmountGross);
                return WithSig(new PayFastNotifyOutcome(false, "Amount mismatch"));
            }

            if (intentMapped == PaymentStatus.Completed)
            {
                // ConvertIntentPaymentToPaidOrderAsync is idempotent —
                // repeated ITN with the same reference returns the
                // already-converted order.
                var conv = await _orderIntentService.ConvertIntentPaymentToPaidOrderAsync(
                    payload.MPaymentId!, paidAtUtc: DateTime.UtcNow,
                    gatewayTransactionId: payload.PfPaymentId,
                    authorizationSnapshot: null,
                    cancellationToken);

                if (!conv.IsSuccess)
                {
                    _logger.LogError(
                        "[PayFastNotifyDebug] Conversion failed for intent {Reference}: {Code} {Message}",
                        payload.MPaymentId, conv.Code, conv.Message);
                    return WithSig(new PayFastNotifyOutcome(false, conv.Message ?? "Intent conversion failed"));
                }

                // Phase 1A — best-effort PayFast token capture (order-intent
                // flow). Runs AFTER the order is materialised; never blocks
                // the conversion. Only when tokenization is enabled for this
                // flow and the ITN carried a token mapping to a claimed user.
                if (_settings.TokenizationEnabled
                    && _settings.TokenizationForOrderIntentsEnabled
                    && !string.IsNullOrWhiteSpace(payload.Token)
                    && intent.ClaimedByUserId is Guid intentUserId
                    && intentUserId != Guid.Empty)
                {
                    await TryCapturePayFastTokenAsync(
                        intentUserId, intent.Email, payload.Token!, payload.MPaymentId, payload.PfPaymentId, cancellationToken);
                }

                return WithSig(new PayFastNotifyOutcome(true,
                    conv.Data?.AlreadyConverted == true
                        ? $"Intent {intent.Id} already converted to order {conv.Data.OrderNumber}."
                        : $"Intent {intent.Id} converted to order {conv.Data?.OrderNumber}."));
            }

            // ITN says failed / cancelled / pending. Mark the intent
            // accordingly so it doesn't linger in Pending forever, and
            // exit — we never materialise an Order for a non-COMPLETE
            // PayFast intent.
            if (intentMapped == PaymentStatus.Failed
                && intent.Status != OrderIntentStatus.ConvertedToOrder
                && intent.Status != OrderIntentStatus.Cancelled)
            {
                intent.Status = OrderIntentStatus.Cancelled;
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "[PayFastNotifyDebug] Intent {Reference} marked Cancelled (PayFast status '{Status}')",
                    payload.MPaymentId, payload.PaymentStatus);
                return WithSig(new PayFastNotifyOutcome(true,
                    $"Intent {intent.Id} marked cancelled (PayFast status '{payload.PaymentStatus}').") );
            }

            return WithSig(new PayFastNotifyOutcome(true,
                $"Intent {intent.Id} ITN stored (PayFast status '{payload.PaymentStatus}'). No state change."));
        }

        // 3b. Invoice-bound lookup (legacy path — invoice payments
        //     from Billing → Pay).
        var initiation = await _dbContext.PaymentInitiations
            .Include(i => i.Payment)
            .Include(i => i.Invoice)
            .FirstOrDefaultAsync(i => i.Provider == PaymentProviderType.PayFast
                                   && i.ProviderReference == payload.MPaymentId,
                                 cancellationToken);

        if (initiation?.Payment is null || initiation.Invoice is null)
        {
            _logger.LogWarning("PayFast ITN for unknown reference {Reference}", payload.MPaymentId);
            return WithSig(new PayFastNotifyOutcome(false, "Unknown reference"));
        }

        // 4. Validate amount
        if (Math.Abs(initiation.Payment.Amount - payload.AmountGross) > 0.01m)
        {
            _logger.LogWarning("PayFast ITN amount mismatch for {Reference}: expected {Expected}, got {Got}",
                payload.MPaymentId, initiation.Payment.Amount, payload.AmountGross);
            return WithSig(new PayFastNotifyOutcome(false, "Amount mismatch"));
        }

        // 5. Store PayFast transaction ID
        if (!string.IsNullOrWhiteSpace(payload.PfPaymentId))
        {
            initiation.Payment.GatewayTransactionId = payload.PfPaymentId;
            initiation.ProviderCheckoutId = payload.PfPaymentId;
        }

        // 6. Map status and apply
        var mapped = MapPayFastStatus(payload.PaymentStatus);
        if (!mapped.HasValue)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("PayFast ITN for {Reference} with non-terminal status '{Status}' — stored, no state change.",
                payload.MPaymentId, payload.PaymentStatus);
            return WithSig(new PayFastNotifyOutcome(true, $"Stored. No state change for status '{payload.PaymentStatus}'."));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var result = await _applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId = initiation.Payment.Id,
            NewStatus = mapped.Value,
            FailureReason = mapped.Value == PaymentStatus.Failed ? (payload.PaymentStatus ?? "Failed") : null,
            TriggerNotifications = true
        }, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogError("ApplyStatusChangeAsync failed for PayFast ITN {Reference}: {Code} {Message}",
                payload.MPaymentId, result.Code, result.Message);
            return WithSig(new PayFastNotifyOutcome(false, result.Message ?? "Apply failed"));
        }

        // Phase 1C2 — reconcile auto-billing rows for an async PayFast
        // settlement (COMPLETE → close attempt Success; FAILED → fail attempt
        // + schedule next retry). Runs AFTER the applier settled the invoice;
        // no-op for non-auto-billing payments; best-effort — never break the
        // ITN response.
        try
        {
            await _autoBilling.ReconcileProviderSettlementAsync(
                initiation.Payment.Id,
                completed: mapped.Value == PaymentStatus.Completed,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[auto-billing][settlement][reconcile] threw for PayFast ITN {Reference}; settlement unaffected.",
                payload.MPaymentId);
        }

        // Phase 1A — best-effort PayFast token capture (invoice-bound flow).
        // Runs AFTER the payment is applied; never blocks settlement. Only on
        // COMPLETE when tokenization is enabled for this flow and the ITN
        // carried a token mapping to the invoice's order owner.
        if (_settings.TokenizationEnabled
            && _settings.TokenizationForInvoicePaymentsEnabled
            && mapped.Value == PaymentStatus.Completed
            && !string.IsNullOrWhiteSpace(payload.Token))
        {
            var orderInfo = await _dbContext.Orders
                .AsNoTracking()
                .Where(o => o.Id == initiation.Invoice.OrderId)
                .Select(o => new { o.UserId, Email = o.Email ?? (o.User != null ? o.User.Email : null) })
                .FirstOrDefaultAsync(cancellationToken);
            if (orderInfo is not null && orderInfo.UserId != Guid.Empty)
            {
                await TryCapturePayFastTokenAsync(
                    orderInfo.UserId, orderInfo.Email, payload.Token!, payload.MPaymentId, payload.PfPaymentId, cancellationToken);
            }
        }

        return WithSig(new PayFastNotifyOutcome(true, $"Payment {initiation.Payment.PaymentNumber} updated to {mapped.Value}."));
    }

    /// <summary>
    /// Phase 1A — best-effort capture of a PayFast reusable token as a
    /// CustomerPaymentMandate. NEVER throws to the caller and NEVER logs the
    /// token value; a failure here must not affect the already-settled
    /// payment. Idempotent via the mandate service.
    /// </summary>
    private async Task TryCapturePayFastTokenAsync(
        Guid userId, string? email, string token, string? mPaymentId, string? pfPaymentId, CancellationToken cancellationToken)
    {
        try
        {
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                source = "PayFast tokenization ITN",
                mPaymentId,
                pfPaymentId
            });

            var res = await _mandates.UpsertPayFastMandateAsync(new UpsertPayFastMandateRequestDto
            {
                UserId = userId,
                Token = token,
                CustomerEmail = email,
                ProviderReference = pfPaymentId ?? mPaymentId,
                ConsentSource = CustomerMandateConsentSource.InstallationCheckout,
                MetadataJson = metadataJson
            }, cancellationToken);

            if (res.IsSuccess)
                _logger.LogInformation(
                    "[PayFastTokenCapture] mandate {MandateId} captured for user {UserId} reference {Reference}",
                    res.Data, userId, mPaymentId);
            else
                _logger.LogWarning(
                    "[PayFastTokenCapture] mandate upsert returned {Code} '{Message}' for reference {Reference}",
                    res.Code, res.Message, mPaymentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PayFastTokenCapture] threw for reference {Reference}; payment settlement unaffected.",
                mPaymentId);
        }
    }

    private static PaymentStatus? MapPayFastStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        return status.Trim().ToUpperInvariant() switch
        {
            "COMPLETE" => PaymentStatus.Completed,
            "FAILED"   => PaymentStatus.Failed,
            "PENDING"  => null,
            _          => null
        };
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildSignatureParams(PayFastNotifyPayload p)
    {
        // PayFast ITN signature: all posted fields EXCEPT signature, in the order received.
        // We rebuild them in the canonical order PayFast documents.
        var pairs = new List<KeyValuePair<string, string>>();
        void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) pairs.Add(new(k, v)); }

        Add("m_payment_id",    p.MPaymentId);
        Add("pf_payment_id",   p.PfPaymentId);
        Add("payment_status",  p.PaymentStatus);
        Add("item_name",       p.ItemName);
        Add("item_description", p.ItemDescription);
        Add("amount_gross",    p.AmountGross > 0 ? PayFastSignatureCalculator.FormatAmount(p.AmountGross) : null);
        Add("amount_fee",      p.AmountFee > 0 ? PayFastSignatureCalculator.FormatAmount(p.AmountFee) : null);
        Add("amount_net",      p.AmountNet > 0 ? PayFastSignatureCalculator.FormatAmount(p.AmountNet) : null);
        Add("custom_str1",     p.CustomStr1);
        Add("custom_str2",     p.CustomStr2);
        Add("custom_str3",     p.CustomStr3);
        Add("custom_str4",     p.CustomStr4);
        Add("custom_str5",     p.CustomStr5);
        Add("custom_int1",     p.CustomInt1?.ToString());
        Add("custom_int2",     p.CustomInt2?.ToString());
        Add("custom_int3",     p.CustomInt3?.ToString());
        Add("custom_int4",     p.CustomInt4?.ToString());
        Add("custom_int5",     p.CustomInt5?.ToString());
        Add("name_first",      p.NameFirst);
        Add("name_last",       p.NameLast);
        Add("email_address",   p.EmailAddress);
        Add("merchant_id",     p.MerchantId);

        return pairs;
    }
}

public class PayFastNotifyOutcome
{
    public bool Accepted { get; }
    public string Message { get; }

    /// <summary>Populated for both pass and fail when posted fields
    /// were supplied. Contains the field order used, redacted base
    /// string, and chosen algorithm. NEVER contains the passphrase.
    /// Forensic capture stores this so signature mismatches can be
    /// reproduced offline.</summary>
    public PayFastItnSignatureDebug? SignatureDebug { get; set; }
    public string? PostedSignature { get; set; }
    public string? ComputedSignature { get; set; }

    public PayFastNotifyOutcome(bool accepted, string message)
    {
        Accepted = accepted;
        Message = message;
    }
}

public class PayFastNotifyPayload
{
    public string? MPaymentId   { get; set; }
    public string? PfPaymentId  { get; set; }
    public string? PaymentStatus { get; set; }
    public string? ItemName     { get; set; }
    public string? ItemDescription { get; set; }
    public decimal AmountGross  { get; set; }
    public decimal AmountFee    { get; set; }
    public decimal AmountNet    { get; set; }
    public string? CustomStr1   { get; set; }
    public string? CustomStr2   { get; set; }
    public string? CustomStr3   { get; set; }
    public string? CustomStr4   { get; set; }
    public string? CustomStr5   { get; set; }
    public int?    CustomInt1   { get; set; }
    public int?    CustomInt2   { get; set; }
    public int?    CustomInt3   { get; set; }
    public int?    CustomInt4   { get; set; }
    public int?    CustomInt5   { get; set; }
    public string? NameFirst    { get; set; }
    public string? NameLast     { get; set; }
    public string? EmailAddress { get; set; }
    public string? MerchantId   { get; set; }
    public string? Signature    { get; set; }

    /// <summary>
    /// Phase 1A — PayFast reusable token returned on a tokenization-setup
    /// payment. SENSITIVE: never logged, never serialized into summary /
    /// forensic JSON, never returned in any DTO. Captured in-memory only,
    /// encrypted on store.
    /// </summary>
    public string? Token { get; set; }
}

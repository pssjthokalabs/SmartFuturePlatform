using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.Payments.Dtos;
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
    private readonly PayFastSettings _settings;
    private readonly ILogger<PayFastNotifyHandler> _logger;

    public PayFastNotifyHandler(
        IAppDbContext dbContext,
        IPaymentApplierService applier,
        IOrderIntentService orderIntentService,
        IOptions<PayFastSettings> settings,
        ILogger<PayFastNotifyHandler> logger)
    {
        _dbContext = dbContext;
        _applier = applier;
        _orderIntentService = orderIntentService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<PayFastNotifyOutcome> HandleAsync(PayFastNotifyPayload payload, CancellationToken cancellationToken)
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
            return new PayFastNotifyOutcome(false, "Merchant ID mismatch");
        }

        // 2. Validate signature
        var signatureParams = BuildSignatureParams(payload);
        var expectedSignature = PayFastSignatureCalculator.GenerateSignature(signatureParams, _settings.Passphrase);
        if (!PayFastSignatureCalculator.SignaturesMatch(expectedSignature, payload.Signature))
        {
            _logger.LogWarning("PayFast ITN signature mismatch for reference {Reference}", payload.MPaymentId);
            return new PayFastNotifyOutcome(false, "Signature mismatch");
        }

        if (string.IsNullOrWhiteSpace(payload.MPaymentId))
            return new PayFastNotifyOutcome(false, "Missing m_payment_id");

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
                return new PayFastNotifyOutcome(false, "Amount mismatch");
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
                    return new PayFastNotifyOutcome(false, conv.Message ?? "Intent conversion failed");
                }

                return new PayFastNotifyOutcome(true,
                    conv.Data?.AlreadyConverted == true
                        ? $"Intent {intent.Id} already converted to order {conv.Data.OrderNumber}."
                        : $"Intent {intent.Id} converted to order {conv.Data?.OrderNumber}.");
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
                return new PayFastNotifyOutcome(true,
                    $"Intent {intent.Id} marked cancelled (PayFast status '{payload.PaymentStatus}').");
            }

            return new PayFastNotifyOutcome(true,
                $"Intent {intent.Id} ITN stored (PayFast status '{payload.PaymentStatus}'). No state change.");
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
            return new PayFastNotifyOutcome(false, "Unknown reference");
        }

        // 4. Validate amount
        if (Math.Abs(initiation.Payment.Amount - payload.AmountGross) > 0.01m)
        {
            _logger.LogWarning("PayFast ITN amount mismatch for {Reference}: expected {Expected}, got {Got}",
                payload.MPaymentId, initiation.Payment.Amount, payload.AmountGross);
            return new PayFastNotifyOutcome(false, "Amount mismatch");
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
            return new PayFastNotifyOutcome(true, $"Stored. No state change for status '{payload.PaymentStatus}'.");
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
            return new PayFastNotifyOutcome(false, result.Message ?? "Apply failed");
        }

        return new PayFastNotifyOutcome(true, $"Payment {initiation.Payment.PaymentNumber} updated to {mapped.Value}.");
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
}

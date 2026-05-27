using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.PayFast;

public class PayFastNotifyHandler
{
    private readonly IAppDbContext _dbContext;
    private readonly IPaymentApplierService _applier;
    private readonly PayFastSettings _settings;
    private readonly ILogger<PayFastNotifyHandler> _logger;

    public PayFastNotifyHandler(IAppDbContext dbContext, IPaymentApplierService applier, IOptions<PayFastSettings> settings, ILogger<PayFastNotifyHandler> logger)
    {
        _dbContext = dbContext;
        _applier = applier;
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

        // 3. Find matching PaymentInitiation
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

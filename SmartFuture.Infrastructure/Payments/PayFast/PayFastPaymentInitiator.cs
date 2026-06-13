using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Infrastructure.Payments.PayFast;

public class PayFastPaymentInitiator : IPaymentInitiator
{
    private readonly PayFastSettings _settings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PayFastPaymentInitiator> _logger;

    public PayFastPaymentInitiator(IOptions<PayFastSettings> settings, IHostEnvironment env, ILogger<PayFastPaymentInitiator> logger)
    {
        _settings = settings.Value;
        _env = env;
        _logger = logger;
    }

    public PaymentProviderType Provider => PaymentProviderType.PayFast;

    public Task<PaymentProviderInitiationResult> InitiateAsync(
        Invoice invoice, Payment payment, InitiateInvoicePaymentRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            return Task.FromResult(PaymentProviderInitiationResult.FailedResult(
                "PayFast is not configured. Set PayFast:MerchantId, PayFast:MerchantKey, PayFast:Passphrase and PayFast:NotifyUrl."));
        }

        var transactionReference = BuildTransactionReference(payment);

        if (transactionReference.Length > 100)
        {
            _logger.LogError("[PayFastRequestDebug] transactionReference too long ({Length} chars): '{Reference}'",
                transactionReference.Length, transactionReference);
            return Task.FromResult(PaymentProviderInitiationResult.FailedResult(
                $"TransactionReference exceeds 100-char limit ({transactionReference.Length} chars)."));
        }

        // ─── test amount override (non-production only) ───────────
        var originalAmount = payment.Amount;
        var overrideActive = _settings.UseTestAmountOverride
                          && _settings.TestAmount is > 0m
                          && !_env.IsProduction();
        if (overrideActive)
        {
            payment.Amount = _settings.TestAmount!.Value;
            _logger.LogWarning(
                "[PayFastTestAmountOverride] invoiceAmount={InvoiceAmount} sentAmount={SentAmount} payment={PaymentNumber} invoice={InvoiceNumber} env={Environment}",
                originalAmount, _settings.TestAmount.Value, payment.PaymentNumber, invoice.InvoiceNumber, _env.EnvironmentName);
        }
        else if (_settings.UseTestAmountOverride && _env.IsProduction())
        {
            _logger.LogError("[PayFastTestAmountOverride] BLOCKED — UseTestAmountOverride is true but environment is Production. Using real amount {Amount}.", originalAmount);
        }

        // Canonical amount log — same shape as the intent initiator
        // and the mobile [payment][payfast][debug] line. Lets a
        // support engineer grep one tag and see EVERY PayFast amount
        // decision regardless of which flow (invoice vs intent).
        _logger.LogInformation(
            "[payment][payfast][amount] originalAmount={Original} effectiveAmount={Effective} useTestAmountOverride={Flag} testAmount={TestAmount} path=invoice env={Env}",
            originalAmount, payment.Amount, _settings.UseTestAmountOverride, _settings.TestAmount, _env.EnvironmentName);

        var amountString = PayFastSignatureCalculator.FormatAmount(payment.Amount);
        var returnUrl = FirstNonEmpty(request.SuccessUrl, _settings.ReturnUrl);
        var cancelUrl = FirstNonEmpty(request.CancelUrl, _settings.CancelUrl);
        var notifyUrl = _settings.NotifyUrl;
        var itemName  = Truncate($"Invoice {invoice.InvoiceNumber}", 100);

        // PayFast parameters in the EXACT order required for signature.
        // Signature is generated from these, then appended.
        var parameters = new List<KeyValuePair<string, string>>
        {
            Kv("merchant_id",  _settings.MerchantId),
            Kv("merchant_key", _settings.MerchantKey),
            Kv("return_url",   returnUrl),
            Kv("cancel_url",   cancelUrl),
            Kv("notify_url",   notifyUrl),
            Kv("m_payment_id", transactionReference),
            Kv("amount",       amountString),
            Kv("item_name",    itemName),
        };

        var signature = PayFastSignatureCalculator.GenerateSignature(parameters, _settings.Passphrase);

        // ─── debug log (non-production only) ──────────────────────
        if (!_env.IsProduction())
        {
            var debugString = PayFastSignatureCalculator.BuildParamString(parameters);
            var masked = debugString
                .Replace(_settings.MerchantKey, "***MASKED***")
                .Replace(PayFastSignatureCalculator.PhpUrlEncode(_settings.MerchantKey), "***MASKED***");
            _logger.LogInformation(
                "[PayFastSignatureDebug] paramString='{ParamString}' passphrasePresent={HasPassphrase} signature={Signature}",
                masked, !string.IsNullOrWhiteSpace(_settings.Passphrase), signature);
        }

        _logger.LogInformation(
            "[PayFastRequestDebug] processUrl={ProcessUrl} merchantId={MerchantIdMasked} m_payment_id={Reference} " +
            "amount={Amount} returnUrl={ReturnUrl} cancelUrl={CancelUrl} notifyUrl={NotifyUrl} sandbox={Sandbox}",
            _settings.ProcessUrl, MaskId(_settings.MerchantId), transactionReference,
            amountString, returnUrl, cancelUrl, notifyUrl, _settings.UseSandbox);

        // Full URLs (grep-able under [payment][payfast][notify_url]).
        // Mirrors the intent initiator so the operator sees the exact
        // notify_url regardless of which flow ran.
        _logger.LogInformation(
            "[payment][payfast][notify_url] notifyUrl={NotifyUrl} returnUrl={ReturnUrl} cancelUrl={CancelUrl} path=invoice",
            notifyUrl, returnUrl, cancelUrl);

        // Build redirect URL. Values are PHP-url-encoded (same encoding
        // used for signature) so PayFast sees identical strings on both
        // sides — no decode/re-encode mismatch.
        parameters.Add(Kv("signature", signature));
        var query = string.Join("&", parameters
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={PayFastSignatureCalculator.PhpUrlEncode(kv.Value.Trim())}"));
        var redirectUrl = $"{_settings.ProcessUrl}?{query}";

        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            transactionReference,
            sandbox = _settings.UseSandbox,
            returnUrl,
            cancelUrl,
            notifyUrl,
            testAmountOverrideApplied = overrideActive,
            testAmountOverrideOriginalAmount = overrideActive ? originalAmount : (decimal?)null,
            testAmountOverrideAmount = overrideActive ? _settings.TestAmount : null
        });

        return Task.FromResult(PaymentProviderInitiationResult.Succeeded(
            providerReference: transactionReference,
            redirectUrl: redirectUrl,
            metadataJson: metadata,
            providerStatusCode: null,
            providerEndpoint: _settings.ProcessUrl,
            providerIsTest: _settings.UseSandbox));
    }

    private static string BuildTransactionReference(Payment payment)
    {
        var paymentRef = !string.IsNullOrWhiteSpace(payment.PaymentNumber) ? payment.PaymentNumber : payment.Id.ToString("N")[..12];
        return $"SF-{paymentRef}";
    }

    private static string FirstNonEmpty(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a! : b ?? string.Empty;
    private static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);
    private static KeyValuePair<string, string> Kv(string key, string value) => new(key, value);

    private static string MaskId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "(empty)";
        if (id.Length <= 4) return new string('*', id.Length);
        return $"{id[..2]}***{id[^2..]}";
    }
}

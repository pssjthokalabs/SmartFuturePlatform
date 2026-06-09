using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.PayFast;

namespace SmartFuture.Infrastructure.Payments.PayFast;

/// <summary>
/// PayFast intent-bound initiator. Mirrors
/// <see cref="Paystack.PaystackIntentInitiationService"/> but for the
/// PayFast hosted-checkout (form-POST redirect, no outbound HTTP call).
///
/// The signed-parameter generation is shared with
/// <see cref="PayFastPaymentInitiator"/> via
/// <see cref="PayFastSignatureCalculator"/>. The reference written into
/// <c>m_payment_id</c> matches the Paystack intent pattern
/// (<c>SF-INTENT-{12-hex}</c>) so the PayFast notify handler can fall
/// back to <c>OrderIntents.IntentPaymentReference</c> when no
/// <c>PaymentInitiation</c> row matches.
///
/// SAFETY: this service NEVER persists. Side-effects are the caller's
/// (the OrderIntent row + the SaveChanges in
/// <c>OrderIntentService.InitiateClientPaymentAsync</c>). Failure to
/// initiate must not leave behind orphan rows on PayFast's side either —
/// PayFast hosted-checkout records the customer "started" the
/// transaction only when they actually land on the payment page, and
/// no charge happens until they confirm.
/// </summary>
public class PayFastIntentInitiationService : IPayFastIntentInitiationService
{
    private readonly PayFastSettings _settings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PayFastIntentInitiationService> _logger;

    public PayFastIntentInitiationService(
        IOptions<PayFastSettings> settings,
        IHostEnvironment env,
        ILogger<PayFastIntentInitiationService> logger)
    {
        _settings = settings.Value;
        _env = env;
        _logger = logger;
    }

    public Task<PayFastIntentInitiationResult> InitiateAsync(
        PayFastIntentInitiationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            return Task.FromResult(PayFastIntentInitiationResult.Fail("Request is required."));

        if (!_settings.Enabled || !_settings.IsConfigured)
        {
            return Task.FromResult(PayFastIntentInitiationResult.Fail(
                "PayFast is not enabled / configured. Check PayFast__Enabled, MerchantId, MerchantKey, Passphrase, NotifyUrl."));
        }

        if (string.IsNullOrWhiteSpace(request.CustomerEmail))
            return Task.FromResult(PayFastIntentInitiationResult.Fail(
                "Cannot start a PayFast intent without a customer email."));

        if (request.InvoiceAmountAtTime <= 0m)
            return Task.FromResult(PayFastIntentInitiationResult.Fail(
                "Installation fee amount must be greater than zero."));

        // Apply the UAT test-amount override the same way the
        // invoice-bound initiator does (off in Production).
        var originalAmount = request.InvoiceAmountAtTime;
        var overrideActive = _settings.UseTestAmountOverride
                          && _settings.TestAmount is > 0m
                          && !_env.IsProduction();
        var amountSent = overrideActive ? _settings.TestAmount!.Value : originalAmount;

        var amountString = PayFastSignatureCalculator.FormatAmount(amountSent);

        var returnUrl = !string.IsNullOrWhiteSpace(request.ReturnUrlOverride)
            ? request.ReturnUrlOverride
            : _settings.ReturnUrl;
        var cancelUrl = !string.IsNullOrWhiteSpace(request.CancelUrlOverride)
            ? request.CancelUrlOverride
            : _settings.CancelUrl;
        var notifyUrl = _settings.NotifyUrl;

        // Use the SAME prefix Paystack uses, so the notify handler's
        // intent lookup is provider-agnostic. The provider on the
        // intent row distinguishes which gateway actually settled.
        var reference = BuildIntentReference();

        var itemName = !string.IsNullOrWhiteSpace(request.ItemName)
            ? Truncate(request.ItemName!, 100)
            : "SmartFuture installation";

        // EXACT order is required for PayFast's signature spec.
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("merchant_id",  _settings.MerchantId),
            new("merchant_key", _settings.MerchantKey),
            new("return_url",   returnUrl),
            new("cancel_url",   cancelUrl),
            new("notify_url",   notifyUrl),
            new("name_first",   request.CustomerFirstName ?? string.Empty),
            new("name_last",    request.CustomerLastName ?? string.Empty),
            new("email_address", request.CustomerEmail.Trim()),
            new("m_payment_id", reference),
            new("amount",       amountString),
            new("item_name",    itemName),
        };

        var signature = PayFastSignatureCalculator.GenerateSignature(parameters, _settings.Passphrase);

        if (overrideActive)
        {
            _logger.LogWarning(
                "[PayFastLiveUatOverride] source=OrderIntent intentId={IntentId} invoiceAmount={InvoiceAmount} sentAmount={SentAmount} env={Env}",
                request.OrderIntentId, originalAmount, amountSent, _env.EnvironmentName);
        }
        else if (_settings.UseTestAmountOverride && _env.IsProduction())
        {
            _logger.LogError(
                "[PayFastTestAmountOverride] BLOCKED — UseTestAmountOverride is true but environment is Production. Using real amount {Amount}.",
                originalAmount);
        }

        // Canonical amount log — same shape as the invoice initiator
        // and the mobile [payment][payfast][debug] line. Confirms the
        // override decision in a single grep-able place.
        _logger.LogInformation(
            "[payment][payfast][amount] originalAmount={Original} effectiveAmount={Effective} useTestAmountOverride={Flag} testAmount={TestAmount} path=intent env={Env}",
            originalAmount, amountSent, _settings.UseTestAmountOverride, _settings.TestAmount, _env.EnvironmentName);

        _logger.LogInformation(
            "[PayFastIntentInitiate] processUrl={ProcessUrl} merchantId={MerchantIdMasked} m_payment_id={Reference} amount={Amount} returnUrl={ReturnUrl} cancelUrl={CancelUrl} notifyUrl={NotifyUrl} sandbox={Sandbox}",
            _settings.ProcessUrl, MaskId(_settings.MerchantId), reference, amountString,
            returnUrl, cancelUrl, notifyUrl, _settings.UseSandbox);

        // Safe config log (verbatim merchant_id so an operator can
        // confirm the value being POSTed to PayFast really matches the
        // PayFast__MerchantId env var). MerchantKey + Passphrase +
        // signature are NEVER logged.
        _logger.LogInformation(
            "[payment][payfast][config] merchantId={MerchantId} useSandbox={UseSandbox} payFastHost={Host} notifyUrlHost={NotifyHost} returnUrlHost={ReturnHost} cancelUrlHost={CancelHost} env={Env}",
            _settings.MerchantId, _settings.UseSandbox,
            SafeHost(_settings.ProcessUrl), SafeHost(notifyUrl), SafeHost(returnUrl), SafeHost(cancelUrl),
            _env.EnvironmentName);

        parameters.Add(new("signature", signature));

        var query = string.Join("&", parameters
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={PayFastSignatureCalculator.PhpUrlEncode(kv.Value.Trim())}"));
        var redirectUrl = $"{_settings.ProcessUrl}?{query}";

        return Task.FromResult(new PayFastIntentInitiationResult
        {
            Success                     = true,
            Reference                   = reference,
            RedirectUrl                 = redirectUrl,
            AmountSent                  = amountSent,
            InvoiceAmountAtTime         = originalAmount,
            IsTestAmountOverrideApplied = overrideActive,
            Currency                    = "ZAR",
            IsSandbox                   = _settings.UseSandbox,
        });
    }

    private static string BuildIntentReference()
    {
        // SAME prefix as Paystack — see notes on
        // <see cref="IPayFastIntentInitiationService"/>.
        var short12 = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        return $"SF-INTENT-{short12}";
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);

    private static string MaskId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "(empty)";
        if (id.Length <= 4) return new string('*', id.Length);
        return $"{id[..2]}***{id[^2..]}";
    }

    private static string SafeHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(empty)";
        try { return new Uri(url).Host; }
        catch { return "(unparseable)"; }
    }
}

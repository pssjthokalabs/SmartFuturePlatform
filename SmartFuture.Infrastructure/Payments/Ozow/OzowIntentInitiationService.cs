using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Ozow;

namespace SmartFuture.Infrastructure.Payments.Ozow;

/// <summary>
/// Ozow initiation for the NEW-ORDER intent flow. See
/// <see cref="IOzowIntentInitiationService"/> for the contract and the
/// webhook-only safety note.
///
/// Differences from <see cref="OzowPaymentInitiator"/> (invoice flow):
///   • No Invoice / Payment / PaymentInitiation exists yet, so there is
///     nothing to mutate — the amount override is computed locally and
///     reported back on the result instead of being written onto a
///     Payment row.
///   • TransactionReference is the caller's SF-INTENT-… reference,
///     verbatim. The invoice flow sends "SF-{paymentNumber}". That prefix
///     difference is what lets OzowNotifyHandler route an inbound
///     notification to the right settlement path.
/// </summary>
public class OzowIntentInitiationService : IOzowIntentInitiationService
{
    private const string IntentReferencePrefix = "SF-INTENT-";

    private readonly OzowRequestSender _sender;
    private readonly OzowSettings _settings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<OzowIntentInitiationService> _logger;

    public OzowIntentInitiationService(
        OzowRequestSender sender,
        IOptions<OzowSettings> settings,
        IHostEnvironment env,
        ILogger<OzowIntentInitiationService> logger)
    {
        _sender   = sender;
        _settings = settings.Value;
        _env      = env;
        _logger   = logger;
    }

    public async Task<OzowIntentInitiationResult> InitiateAsync(
        OzowIntentInitiationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            return OzowIntentInitiationResult.Fail("Request is required.");

        if (!_settings.IsConfigured)
        {
            // Same fail-fast shape the invoice initiator uses. The caller
            // maps this onto a clean customer-facing message; the detail
            // here is for the operator reading logs.
            return OzowIntentInitiationResult.Fail(
                "Ozow is not configured. Set Ozow:SiteCode, Ozow:ApiKey, Ozow:PrivateKey, Ozow:NotifyUrl and Ozow:IsTest.");
        }

        var reference = (request.Reference ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(reference))
            return OzowIntentInitiationResult.Fail("Intent payment reference is required.");

        // Guard the contract the notify handler depends on. If a future
        // change alters the reference format, fail here rather than
        // silently minting a payment the webhook can't route.
        if (!reference.StartsWith(IntentReferencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "[OzowIntentDebug] reference '{Reference}' does not start with {Prefix} — OzowNotifyHandler routes intent " +
                "notifications by that prefix, so this payment would settle down the invoice path and fail. Refusing to initiate.",
                reference, IntentReferencePrefix);
            return OzowIntentInitiationResult.Fail("Internal reference format error. Please try again or choose another payment method.");
        }

        if (reference.Length > 50)
        {
            _logger.LogError(
                "[OzowIntentDebug] transactionReference too long ({Length} chars, max 50): '{Reference}'",
                reference.Length, reference);
            return OzowIntentInitiationResult.Fail(
                $"TransactionReference exceeds Ozow's 50-char limit ({reference.Length} chars).");
        }

        // IsConfigured guarantees HasValue.
        var isTest = _settings.IsTest!.Value;

        // ─── TEMPORARY LIVE OZOW TEST AMOUNT OVERRIDE ─────────────────
        // Same gate as the invoice path: only outside Production, only
        // when explicitly enabled with a positive amount. There is no
        // Payment row to mirror it onto yet — the overridden value is
        // what we SEND, and ConvertIntentPaymentToPaidOrderAsync later
        // materialises the Payment from IntentPaymentAmount, so the
        // webhook's amount check still lines up.
        var originalAmount = request.InvoiceAmountAtTime;
        var amountSent     = originalAmount;
        var overrideActive = _settings.UseTestAmountOverride
                          && _settings.TestAmount is > 0m
                          && !_env.IsProduction();
        if (overrideActive)
        {
            amountSent = _settings.TestAmount!.Value;
            _logger.LogWarning(
                "[OzowTestAmountOverride] path=intent intentId={IntentId} reference={Reference} " +
                "invoiceAmount={InvoiceAmount} sentAmount={SentAmount} env={Environment}",
                request.OrderIntentId, reference, originalAmount, amountSent, _env.EnvironmentName);
        }
        else if (_settings.UseTestAmountOverride && _env.IsProduction())
        {
            _logger.LogError(
                "[OzowTestAmountOverride] BLOCKED — UseTestAmountOverride is true but environment is Production. " +
                "Using real amount {Amount}. Remove Ozow__UseTestAmountOverride before production.",
                originalAmount);
        }
        // ───────────────────────────────────────────────────────────────

        if (amountSent <= 0m)
        {
            // Ozow rejects non-positive amounts; catching it here gives a
            // far clearer log line than "Invalid Amount" from the gateway.
            return OzowIntentInitiationResult.Fail("Payment amount must be greater than zero.");
        }

        var successUrl = FirstNonEmpty(request.SuccessUrlOverride, _settings.SuccessUrl);
        var cancelUrl  = FirstNonEmpty(request.CancelUrlOverride,  _settings.CancelUrl);
        // Ozow requires a distinct error URL. Fall back to the cancel URL
        // when neither an override nor config supplies one — the portal's
        // /payment/result handles both the same way (poll intent status),
        // so this degrades cleanly rather than blocking checkout.
        var errorUrl   = FirstNonEmpty(request.ErrorUrlOverride, _settings.ErrorUrl);
        if (string.IsNullOrWhiteSpace(errorUrl)) errorUrl = cancelUrl;

        var notifyUrl = _settings.NotifyUrl;

        if (string.IsNullOrWhiteSpace(successUrl) || string.IsNullOrWhiteSpace(cancelUrl) || string.IsNullOrWhiteSpace(errorUrl))
        {
            return OzowIntentInitiationResult.Fail(
                "Ozow success/cancel/error URLs are required. Pass them on the initiate request or configure Ozow:SuccessUrl / Ozow:CancelUrl / Ozow:ErrorUrl.");
        }

        if (string.IsNullOrWhiteSpace(notifyUrl))
        {
            // Hard stop. Without a notify URL an intent payment can NEVER
            // convert to an order — the customer would be charged for
            // nothing. Better to refuse checkout than to take the money.
            _logger.LogError(
                "[OzowIntentDebug] Ozow:NotifyUrl is empty — an order-intent payment could never be converted to an Order. Refusing to initiate.");
            return OzowIntentInitiationResult.Fail(
                "Ozow is not fully configured (missing notify URL). Please choose another payment method.");
        }

        var outcome = await _sender.SendAsync(new OzowRequestSpec
        {
            Amount               = amountSent,
            TransactionReference = reference,
            BankReference        = BuildBankReference(request.BankReferenceLabel),
            SuccessUrl           = successUrl,
            CancelUrl            = cancelUrl,
            ErrorUrl             = errorUrl,
            NotifyUrl            = notifyUrl,
            IsTest               = isTest,
            Flow                 = "intent"
        }, cancellationToken);

        if (!outcome.Success)
        {
            _logger.LogWarning(
                "[OzowIntentDebug] initiate-failed intentId={IntentId} reference={Reference} reason={Reason} httpStatus={StatusCode}",
                request.OrderIntentId, reference, outcome.FailureReason, outcome.ProviderStatusCode);

            return new OzowIntentInitiationResult
            {
                Success             = false,
                FailureReason       = outcome.FailureReason ?? "Ozow initiation failed.",
                Reference           = reference,
                InvoiceAmountAtTime = originalAmount,
                AmountSent          = amountSent,
                IsTest              = isTest
            };
        }

        // ─── [OzowIntentReconcile] ────────────────────────────────────
        // The reconciliation breadcrumb. Emitted the moment Ozow accepts
        // the request — i.e. the moment the customer CAN be charged.
        // If the notification never arrives, this line plus the
        // paymentRequestId is what lets an operator find the transaction
        // in the Ozow dashboard and convert the intent by hand. Do not
        // remove it, and do not downgrade it below Information.
        _logger.LogInformation(
            "[OzowIntentReconcile] stage=initiated intentId={IntentId} reference={Reference} " +
            "ozowPaymentRequestId={PaymentRequestId} amountSent={AmountSent} invoiceAmount={InvoiceAmount} " +
            "override={Override} isTest={IsTest} notifyUrl={NotifyUrl}",
            request.OrderIntentId, reference, outcome.PaymentRequestId ?? "(none)",
            amountSent, originalAmount, overrideActive, isTest, notifyUrl);

        return new OzowIntentInitiationResult
        {
            Success                     = true,
            Reference                   = reference,
            RedirectUrl                 = outcome.RedirectUrl,
            PaymentRequestId            = outcome.PaymentRequestId,
            AmountSent                  = amountSent,
            InvoiceAmountAtTime         = originalAmount,
            IsTestAmountOverrideApplied = overrideActive,
            Currency                    = _settings.CurrencyCode,
            IsTest                      = isTest
        };
    }

    private static string FirstNonEmpty(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a! : b ?? string.Empty;

    // Ozow caps BankReference at 20 chars. There is no invoice number yet
    // (that's the whole point of the intent flow), so we use the package
    // label the caller passed, falling back to the brand.
    private static string BuildBankReference(string? label)
    {
        var candidate = string.IsNullOrWhiteSpace(label) ? "SmartFuture" : label.Trim();
        return candidate.Length > 20 ? candidate[..20] : candidate;
    }
}

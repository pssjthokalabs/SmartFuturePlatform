using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Infrastructure.Payments.Ozow;

/// <summary>
/// Phase 52 — real Ozow PostPaymentRequest initiator for INVOICE
/// payments (an Invoice + Payment + PaymentInitiation already exist;
/// <see cref="OzowNotifyHandler"/> settles them).
///
/// The merchant private key never leaves the API tier. Hash + HTTP
/// transport live in <see cref="OzowRequestSender"/>, shared with the
/// new-order intent flow (<see cref="OzowIntentInitiationService"/>) so
/// both sign identically. This class owns only the invoice-specific
/// decisions: which amount to charge, what to call the transaction, and
/// how to map the outcome onto <see cref="PaymentProviderInitiationResult"/>.
///
/// Coexists with the legacy mock-checkout flow: callers select Ozow by
/// passing <see cref="PaymentProviderType.Ozow"/>; mock-checkout callers
/// still hit <c>OrderService.PersistMockCheckoutAsync</c>.
/// </summary>
public class OzowPaymentInitiator : IPaymentInitiator
{
    private readonly OzowRequestSender _sender;
    private readonly OzowSettings _settings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<OzowPaymentInitiator> _logger;

    public OzowPaymentInitiator(
        OzowRequestSender sender,
        IOptions<OzowSettings> settings,
        IHostEnvironment env,
        ILogger<OzowPaymentInitiator> logger)
    {
        _sender   = sender;
        _settings = settings.Value;
        _env      = env;
        _logger   = logger;
    }

    public PaymentProviderType Provider => PaymentProviderType.Ozow;

    public async Task<PaymentProviderInitiationResult> InitiateAsync(Invoice invoice, Payment payment, InitiateInvoicePaymentRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            return PaymentProviderInitiationResult.FailedResult(
                "Ozow is not configured. Set Ozow:SiteCode, Ozow:ApiKey, Ozow:PrivateKey, Ozow:NotifyUrl and Ozow:IsTest.");
        }

        // Resolved once + reused so the hash, body, and metadata all
        // see the same value. IsConfigured guarantees HasValue above.
        var isTest = _settings.IsTest!.Value;

        var transactionReference = BuildTransactionReference(payment);
        var bankReference        = BuildBankReference(invoice, payment);

        if (transactionReference.Length > 50)
        {
            _logger.LogError(
                "[OzowRequestDebug] transactionReference too long ({Length} chars, max 50): '{Reference}'",
                transactionReference.Length, transactionReference);
            return PaymentProviderInitiationResult.FailedResult(
                $"TransactionReference exceeds Ozow's 50-char limit ({transactionReference.Length} chars).");
        }

        // ─── TEMPORARY LIVE OZOW TEST AMOUNT OVERRIDE ─────────────────
        // Gated on: UseTestAmountOverride=true AND TestAmount > 0 AND
        // environment is NOT Production. Production hard-blocks even if
        // the flags are accidentally left set.
        //
        // Mirrors the override onto payment.Amount so the webhook's
        // amount check passes (R10 vs R10). The invoice total stays
        // untouched — it ends up PartiallyPaid (R10 of R100).
        var originalAmount = payment.Amount;
        var overrideActive = _settings.UseTestAmountOverride
                          && _settings.TestAmount is > 0m
                          && !_env.IsProduction();
        if (overrideActive)
        {
            payment.Amount = _settings.TestAmount!.Value;
            _logger.LogWarning(
                "[OzowTestAmountOverride] invoiceAmount={InvoiceAmount} sentAmount={SentAmount} " +
                "payment={PaymentNumber} invoice={InvoiceNumber} env={Environment}",
                originalAmount, _settings.TestAmount.Value,
                payment.PaymentNumber, invoice.InvoiceNumber, _env.EnvironmentName);
        }
        else if (_settings.UseTestAmountOverride && _env.IsProduction())
        {
            _logger.LogError(
                "[OzowTestAmountOverride] BLOCKED — UseTestAmountOverride is true but environment is Production. " +
                "Using real invoice amount {Amount}. Remove Ozow__UseTestAmountOverride before production.",
                originalAmount);
        }
        // ───────────────────────────────────────────────────────────────

        var successUrl = FirstNonEmpty(request.SuccessUrl, _settings.SuccessUrl);
        var cancelUrl  = FirstNonEmpty(request.CancelUrl,  _settings.CancelUrl);
        var errorUrl   = FirstNonEmpty(request.FailureUrl, _settings.ErrorUrl);
        var notifyUrl  = _settings.NotifyUrl;

        if (string.IsNullOrWhiteSpace(successUrl) || string.IsNullOrWhiteSpace(cancelUrl) || string.IsNullOrWhiteSpace(errorUrl))
        {
            return PaymentProviderInitiationResult.FailedResult(
                "Ozow success/cancel/error URLs are required. Pass them on the initiate request or configure Ozow:SuccessUrl / Ozow:CancelUrl / Ozow:ErrorUrl.");
        }

        var outcome = await _sender.SendAsync(new OzowRequestSpec
        {
            Amount               = payment.Amount,
            TransactionReference = transactionReference,
            BankReference        = bankReference,
            SuccessUrl           = successUrl,
            CancelUrl            = cancelUrl,
            ErrorUrl             = errorUrl,
            NotifyUrl            = notifyUrl,
            IsTest               = isTest,
            Flow                 = "invoice"
        }, cancellationToken);

        if (!outcome.Success)
        {
            return PaymentProviderInitiationResult.FailedResult(
                failureReason:              outcome.FailureReason ?? "Ozow initiation failed.",
                providerStatusCode:         outcome.ProviderStatusCode,
                providerErrorMessage:       outcome.ProviderErrorMessage,
                providerEndpoint:           outcome.Endpoint,
                providerIsTest:             isTest,
                providerReference:          transactionReference,
                providerRawResponseSnippet: outcome.RawBodySnippet);
        }

        // Snapshot the resolved transactionReference into MetadataJson
        // so the webhook handler can sanity-match it on inbound, and so
        // admin can re-issue the same hash for diagnostics. The override
        // fields ride along so the audit trail captures "this was a
        // test-amount-overridden charge" forever.
        var metadata = JsonSerializer.Serialize(new
        {
            transactionReference,
            bankReference,
            isTest,
            ozowPaymentRequestId = outcome.PaymentRequestId,
            successUrl,
            cancelUrl,
            errorUrl,
            notifyUrl,
            testAmountOverrideApplied        = overrideActive,
            testAmountOverrideOriginalAmount = overrideActive ? originalAmount : (decimal?)null,
            testAmountOverrideAmount         = overrideActive ? _settings.TestAmount : null
        });

        return PaymentProviderInitiationResult.Succeeded(
            providerReference:  transactionReference,
            providerCheckoutId: outcome.PaymentRequestId,
            redirectUrl:        outcome.RedirectUrl,
            expiresAtUtc:       null,
            metadataJson:       metadata,
            providerStatusCode: outcome.ProviderStatusCode,
            providerEndpoint:   outcome.Endpoint,
            providerIsTest:     isTest);
    }

    private static string FirstNonEmpty(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a! : b ?? string.Empty;

    // Ozow caps TransactionReference at 50 characters. We use the
    // payment number (unique per Payment row) prefixed with "SF-".
    // This is short, unique, and traceable. The webhook handler matches
    // by PaymentInitiation.ProviderReference, which stores this exact
    // string.
    //
    // NOTE: the new-order intent flow deliberately sends the raw
    // SF-INTENT-… reference instead of an "SF-" prefixed payment number,
    // so OzowNotifyHandler can tell the two apart by prefix. Do not
    // change this format without updating that handler.
    private static string BuildTransactionReference(Payment payment)
    {
        var paymentRef = !string.IsNullOrWhiteSpace(payment.PaymentNumber) ? payment.PaymentNumber : payment.Id.ToString("N")[..12];
        var candidate = $"SF-{paymentRef}";
        return candidate.Length > 50 ? candidate[..50] : candidate;
    }

    // Ozow shows BankReference on the customer's bank statement; max
    // 20 chars per Ozow docs, alphanumeric + dash/underscore safe.
    // We use the invoice number when short enough, falling back to the
    // payment number — both already pass that profile.
    private static string BuildBankReference(Invoice invoice, Payment payment)
    {
        var candidate = !string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? invoice.InvoiceNumber
            : payment.PaymentNumber;
        if (string.IsNullOrWhiteSpace(candidate)) candidate = "SmartFuture";
        return candidate.Length > 20 ? candidate[..20] : candidate;
    }
}

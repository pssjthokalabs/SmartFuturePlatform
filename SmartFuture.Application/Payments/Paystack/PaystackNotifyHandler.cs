using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Handles Paystack's webhook POST. Signature, amount, currency and
/// reference all get cross-checked against server state before any
/// payment is marked Completed. Idempotent: replaying the same event
/// is a no-op once the matching Payment is already Completed.
///
/// Lives in Application so the controller is a thin AllowAnonymous
/// shell that just reads the raw body + <c>x-paystack-signature</c>
/// header and hands them here.
/// </summary>
public class PaystackNotifyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppDbContext _dbContext;
    private readonly IPaymentApplierService _applier;
    private readonly PaystackSettings _settings;
    private readonly PaymentProcessingSettings _processingSettings;
    private readonly PaystackVerificationService _verifier;
    private readonly ICustomerPaymentMandateService _mandates;
    private readonly IHostEnvironment _env;
    private readonly OrderIntents.IOrderIntentService _orderIntentService;
    private readonly ILogger<PaystackNotifyHandler> _logger;

    public PaystackNotifyHandler(
        IAppDbContext dbContext,
        IPaymentApplierService applier,
        IOptions<PaystackSettings> settings,
        IOptions<PaymentProcessingSettings> processingSettings,
        PaystackVerificationService verifier,
        ICustomerPaymentMandateService mandates,
        IHostEnvironment env,
        OrderIntents.IOrderIntentService orderIntentService,
        ILogger<PaystackNotifyHandler> logger)
    {
        _dbContext = dbContext;
        _applier = applier;
        _settings = settings.Value;
        _processingSettings = processingSettings.Value;
        _verifier = verifier;
        _mandates = mandates;
        _env = env;
        _orderIntentService = orderIntentService;
        _logger = logger;
    }

    /// <summary>
    /// Validate + apply. Pass the **raw** JSON request body and the
    /// <c>x-paystack-signature</c> header value. Always returns
    /// <see cref="PaystackNotifyOutcome"/>; the controller wraps that
    /// in an HTTP 200.
    ///
    /// Every call now persists a <see cref="PaystackWebhookLog"/> row
    /// no matter the outcome — the row is mutated as the handler walks
    /// through each gate, and saved in a finally block. The returned
    /// outcome carries the log's Id as <c>DiagnosticId</c> so the
    /// controller can stamp it in the response body and an admin can
    /// look it up by reference.
    /// </summary>
    public async Task<PaystackNotifyOutcome> HandleAsync(string rawBody, string? signatureHeader, CancellationToken cancellationToken)
    {
        // Loud receipt log — independent of signature outcome — so a
        // missing webhook is always distinguishable from a rejected
        // one in log aggregation. Body length only, never raw body.
        _logger.LogInformation(
            "[PaystackWebhookReceived] bodyBytes={BodyBytes} signaturePresent={SigPresent}",
            rawBody?.Length ?? 0, !string.IsNullOrWhiteSpace(signatureHeader));

        // Forensic row written for every webhook call. Mutated as we
        // walk through each gate; persisted in the finally block.
        var log = new PaystackWebhookLog
        {
            Provider         = PaymentProviderType.Paystack,
            ReceivedAtUtc    = DateTime.UtcNow,
            RawBodyLength    = rawBody?.Length ?? 0,
            SignaturePresent = !string.IsNullOrWhiteSpace(signatureHeader),
            EnvironmentName  = _env.EnvironmentName,
            HttpStatusReturned = 200,
        };

        try
        {
            // ─── Pre-signature gates ─────────────────────────────────
            if (string.IsNullOrWhiteSpace(rawBody))
            {
                _logger.LogWarning("[PaystackWebhookRejected] reason=empty-body");
                return Reject(log, "empty-body", "Empty body");
            }
            if (!_settings.Enabled)
            {
                _logger.LogWarning("[PaystackWebhookRejected] reason=paystack-not-enabled");
                return Reject(log, "paystack-not-enabled", "Paystack not enabled");
            }
            if (!_settings.IsConfigured)
            {
                _logger.LogWarning("[PaystackWebhookRejected] reason=paystack-not-configured");
                return Reject(log, "paystack-not-configured", "Paystack not configured");
            }

            // 1) Signature — HMAC-SHA512(rawBody, secretKey), constant-time.
            log.SignatureValid = IsSignatureValid(rawBody!, signatureHeader, _settings.SecretKey);
            if (!log.SignatureValid)
            {
                _logger.LogWarning(
                    "[PaystackWebhookRejected] reason=signature-mismatch keyPrefix={KeyPrefix} bodyBytes={BodyBytes}",
                    ResolveSecretKeyDiagnosticPrefix(_settings.SecretKey), rawBody!.Length);
                return Reject(log, "signature-mismatch", "Signature mismatch");
            }

            PaystackEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<PaystackEvent>(rawBody!, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PaystackNotify] malformed JSON body");
                return Reject(log, "malformed-json", "Malformed JSON");
            }
            if (evt is null || evt.Data is null)
                return Reject(log, "missing-event-or-data", "Missing event/data");

            log.Event          = evt.Event;
            log.Reference      = evt.Data.Reference;
            log.AmountSubunits = evt.Data.Amount;
            log.Currency       = evt.Data.Currency;
            log.Status         = evt.Data.Status;

            // 2) Non-actionable events — acknowledge without applying.
            if (!string.Equals(evt.Event, "charge.success", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[PaystackNotify] non-actionable event '{Event}' reference='{Reference}' — acknowledged without state change",
                    evt.Event, evt.Data.Reference);
                return AcceptNoOp(log, "non-actionable-event", $"Event '{evt.Event}' acknowledged (no state change).");
            }

            if (string.IsNullOrWhiteSpace(evt.Data.Reference))
                return Reject(log, "missing-reference", "Missing reference");

            // Phase 53 — SF-INTENT-… references belong to the new
            // "Order and Pay" flow. No PaymentInitiation exists for
            // these YET — the convert path creates Order + Invoice +
            // Payment + PaymentInitiation atomically. Route here before
            // the lookup so we don't reject as unknown-reference.
            if (OrderIntents.OrderIntentService.IsIntentReference(evt.Data.Reference))
            {
                var convert = await _orderIntentService.ConvertIntentPaymentToPaidOrderAsync(
                    evt.Data.Reference, evt.Data.PaidAt ?? DateTime.UtcNow,
                    evt.Data.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken);

                if (convert.IsSuccess && convert.Data is not null)
                {
                    log.InvoiceId = convert.Data.InvoiceId;
                    log.PaymentId = convert.Data.PaymentId;
                    log.ApplyAttempted = true;
                    log.ApplySucceeded = true;
                    return AcceptNoOp(log, "intent-converted",
                        $"OrderIntent converted to Order {convert.Data.OrderNumber} (invoice {convert.Data.InvoiceNumber}).");
                }

                log.ApplyAttempted = true;
                log.ApplyErrorCode = convert.Code;
                log.ApplyErrorMessage = convert.Message;
                _logger.LogError(
                    "[PaystackWebhookApply] intent-convert failed reference={Reference} code={Code} message='{Message}'",
                    evt.Data.Reference, convert.Code, convert.Message);
                return Reject(log, "intent-convert-failed", convert.Message ?? "Intent convert failed.");
            }

            // 3) Lookup PaymentInitiation by Paystack reference.
            var initiation = await _dbContext.PaymentInitiations
                .Include(i => i.Payment)
                .Include(i => i.Invoice)
                .FirstOrDefaultAsync(i => i.Provider == PaymentProviderType.Paystack
                                       && i.ProviderReference == evt.Data.Reference,
                                     cancellationToken);

            if (initiation is null || initiation.Payment is null || initiation.Invoice is null)
            {
                _logger.LogWarning(
                    "[PaystackWebhookRejected] reason=unknown-reference reference={Reference}",
                    evt.Data.Reference);
                return Reject(log, "unknown-reference", "Unknown reference");
            }

            log.PaymentInitiationId = initiation.Id;
            log.PaymentId           = initiation.Payment.Id;
            log.InvoiceId           = initiation.InvoiceId;

            // 4) Idempotency — already-Completed payment is a no-op.
            if (initiation.Payment.Status == PaymentStatus.Completed)
            {
                _logger.LogInformation(
                    "[PaystackNotify] payment {PaymentNumber} already Completed — webhook acknowledged without re-applying",
                    initiation.Payment.PaymentNumber);
                return AcceptNoOp(log, "already-completed", "Payment already Completed.");
            }

            // 5) Cross-checks: currency + amount + transaction status.
            var webhookCurrency  = (evt.Data.Currency ?? string.Empty).Trim().ToUpperInvariant();
            var expectedCurrency = (_settings.Currency ?? "ZAR").Trim().ToUpperInvariant();
            if (!string.Equals(webhookCurrency, expectedCurrency, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "[PaystackWebhookRejected] reason=currency-mismatch reference={Reference} expected={Expected} got={Got}",
                    evt.Data.Reference, expectedCurrency, webhookCurrency);
                return Reject(log, "currency-mismatch", "Currency mismatch");
            }

            var expectedSubunits = ToSubunits(initiation.Payment.Amount);
            if (evt.Data.Amount != expectedSubunits)
            {
                _logger.LogWarning(
                    "[PaystackWebhookRejected] reason=amount-mismatch reference={Reference} expectedSubunits={Expected} gotSubunits={Got}",
                    evt.Data.Reference, expectedSubunits, evt.Data.Amount);
                return Reject(log, "amount-mismatch", "Amount mismatch");
            }

            if (!string.Equals(evt.Data.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[PaystackNotify] event status '{Status}' for {Reference} — not applying Completed",
                    evt.Data.Status, evt.Data.Reference);
                return AcceptNoOp(log, "status-not-success", $"Event status '{evt.Data.Status}' recorded; no state change.");
            }

            // 6) Optional belt-and-braces /transaction/verify call.
            var verifyResult = await _verifier.VerifyAsync(evt.Data.Reference, cancellationToken);
            if (verifyResult.IsSuccess)
            {
                var v = verifyResult.Data!;
                if (!string.Equals(v.Status, "success", StringComparison.OrdinalIgnoreCase)
                    || v.AmountSubunits != expectedSubunits
                    || (!string.IsNullOrWhiteSpace(v.Currency)
                        && !string.Equals(v.Currency, expectedCurrency, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning(
                        "[PaystackNotify] verify-call disagreement for {Reference}: verifyStatus={Status} verifyAmount={Amount} verifyCurrency={Currency}",
                        evt.Data.Reference, v.Status, v.AmountSubunits, v.Currency);
                    return Reject(log, "verify-disagreement", "Verify disagreement");
                }
            }
            else
            {
                _logger.LogWarning(
                    "[PaystackNotify] verify call failed for {Reference}: {Message} — proceeding on signed webhook only",
                    evt.Data.Reference, verifyResult.Message);
            }

            // 7) Persist Paystack's transaction id + record webhook receipt.
            var paystackTransactionId = evt.Data.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(paystackTransactionId))
            {
                initiation.Payment.GatewayTransactionId = paystackTransactionId;
                initiation.ProviderCheckoutId = paystackTransactionId;
            }
            initiation.WebhookLastReceivedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            // 8) Mandate upsert (best-effort, never blocks apply).
            var mandateUpsertedNote = await TryUpsertPaystackMandateAsync(initiation, evt.Data, cancellationToken);

            // 9) Dry-run / kill-switch gating.
            var applyAllowed = _processingSettings.WebhookApplyEnabled
                            && initiation.WebhookApplyMode == WebhookApplyMode.ApplyNormally;
            if (!applyAllowed)
            {
                var reason = !_processingSettings.WebhookApplyEnabled
                    ? "PaymentProcessing.WebhookApplyEnabled=false"
                    : $"PaymentInitiation.WebhookApplyMode={initiation.WebhookApplyMode}";
                _logger.LogInformation(
                    "[PaystackWebhookDryRun] validated=true reference={Reference} applicationSuppressed=true reason='{Reason}' mandate='{Mandate}'",
                    evt.Data.Reference, reason, mandateUpsertedNote ?? "(none)");
                return AcceptNoOp(log, $"apply-suppressed:{reason}",
                    $"Validated; application suppressed ({reason}).{(mandateUpsertedNote is null ? "" : $" {mandateUpsertedNote}")}");
            }

            // 10) Apply Completed via PaymentApplierService (idempotent).
            log.ApplyAttempted = true;
            var statusBefore = initiation.Invoice.Status.ToString();
            var result = await _applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
            {
                PaymentId = initiation.Payment.Id,
                NewStatus = PaymentStatus.Completed,
                GatewayTransactionId = paystackTransactionId,
                GatewayReference = evt.Data.Reference,
                PaidAtUtc = evt.Data.PaidAt ?? DateTime.UtcNow,
                TriggerNotifications = true
            }, cancellationToken);

            if (!result.IsSuccess)
            {
                log.ApplyErrorCode    = result.Code;
                log.ApplyErrorMessage = Truncate(result.Message, 500);
                _logger.LogError(
                    "[PaystackWebhookApply] apply-failed reference={Reference} paymentInitiationId={InitiationId} invoiceId={InvoiceId} code={Code} message='{Message}'",
                    evt.Data.Reference, initiation.Id, initiation.InvoiceId, result.Code, result.Message);
                return Reject(log, "apply-failed", result.Message ?? "Apply failed");
            }
            log.ApplySucceeded = true;

            // Re-read post-commit so the structured log shows the after-state.
            var statusAfter = await _dbContext.Invoices
                .AsNoTracking()
                .Where(i => i.Id == initiation.InvoiceId)
                .Select(i => i.Status)
                .FirstOrDefaultAsync(cancellationToken);

            _logger.LogInformation(
                "[PaystackWebhookApply] applied reference={Reference} paymentInitiationId={InitiationId} invoiceId={InvoiceId} applyMode={ApplyMode} overrideApplied={Override} beforeStatus={Before} afterStatus={After}",
                evt.Data.Reference, initiation.Id, initiation.InvoiceId, initiation.WebhookApplyMode,
                initiation.IsTestAmountOverrideApplied, statusBefore, statusAfter);

            log.Accepted = true;
            var successMessage = Truncate(
                $"Payment {initiation.Payment.PaymentNumber} → Completed; invoice {statusBefore} → {statusAfter}.{(mandateUpsertedNote is null ? "" : $" {mandateUpsertedNote}")}",
                500) ?? "Applied.";
            log.OutcomeMessage = successMessage;
            return new PaystackNotifyOutcome(true, successMessage, log.Id, log.Reference);
        }
        catch (Exception ex)
        {
            // Unhandled — record everything we know and bubble a 200
            // so Paystack doesn't enter a retry loop on a code defect.
            _logger.LogError(ex, "[PaystackNotify] unexpected exception during handler.");
            log.RejectionReason   = "handler-exception";
            log.OutcomeMessage    = Truncate(ex.Message, 500);
            log.ApplyErrorMessage = Truncate(ex.GetType().Name, 500);
            return new PaystackNotifyOutcome(false, "Handler exception (logged).", log.Id, log.Reference);
        }
        finally
        {
            // ALWAYS save the log row — even when an earlier
            // SaveChanges call may already have run. PaystackWebhookLog
            // is independent of any tracked entity so this is a single
            // insert with no FK on the failing row.
            try
            {
                _dbContext.PaystackWebhookLogs.Add(log);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx,
                    "[PaystackWebhookLog] could not persist diagnostic row for reference={Reference}",
                    log.Reference);
            }
        }
    }

    // Reject helper — stamps the rejection reason, leaves Accepted=false.
    private static PaystackNotifyOutcome Reject(PaystackWebhookLog log, string reason, string message)
    {
        log.Accepted        = false;
        log.RejectionReason = reason;
        log.OutcomeMessage  = Truncate(message, 500);
        return new PaystackNotifyOutcome(false, message, log.Id, log.Reference);
    }

    // Accept-no-op helper — webhook accepted (200) but no state change.
    private static PaystackNotifyOutcome AcceptNoOp(PaystackWebhookLog log, string reason, string message)
    {
        log.Accepted        = true;
        log.RejectionReason = reason; // re-purposed as "no-op reason"
        log.OutcomeMessage  = Truncate(message, 500);
        return new PaystackNotifyOutcome(true, message, log.Id, log.Reference);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }

    // Returns "sk_test", "sk_live", or "(unknown)" — never the actual
    // key. Used in [PaystackWebhookRejected] so an operator can
    // immediately see whether the signature mismatch is plausibly a
    // test-vs-live key mix-up without leaking the secret.
    private static string ResolveSecretKeyDiagnosticPrefix(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "(none)";
        if (key.StartsWith("sk_test_", StringComparison.OrdinalIgnoreCase)) return "sk_test";
        if (key.StartsWith("sk_live_", StringComparison.OrdinalIgnoreCase)) return "sk_live";
        return "(unknown)";
    }

    // Best-effort mandate upsert. We only act when the event carried a
    // reusable authorization and we can resolve the SmartFuture user
    // that owns the invoice. Failures are logged but never abort the
    // payment apply — the apply path is the customer-visible one.
    private async Task<string?> TryUpsertPaystackMandateAsync(
        Domain.Billing.PaymentInitiation initiation,
        PaystackEventData data,
        CancellationToken cancellationToken)
    {
        if (data.Authorization is null) return null;
        if (data.Authorization.Reusable != true || string.IsNullOrWhiteSpace(data.Authorization.AuthorizationCode))
        {
            _logger.LogInformation(
                "[PaystackMandate] event reference={Reference} returned non-reusable authorization — skipping mandate store",
                data.Reference);
            return null;
        }

        // Resolve the SmartFuture user via the linked invoice → order.
        var invoiceUserId = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.Id == initiation.InvoiceId)
            .Select(i => i.Order != null ? i.Order.UserId : (Guid?)null)
            .FirstOrDefaultAsync(cancellationToken);
        if (invoiceUserId is null || invoiceUserId == Guid.Empty)
        {
            _logger.LogWarning(
                "[PaystackMandate] cannot resolve user for invoice {InvoiceId} — mandate not stored",
                initiation.InvoiceId);
            return null;
        }

        var request = new UpsertPaystackMandateRequestDto
        {
            UserId = invoiceUserId.Value,
            AuthorizationCode = data.Authorization.AuthorizationCode!,
            AuthorizationSignature = data.Authorization.Signature,
            Channel = data.Authorization.Channel,
            CardType = data.Authorization.CardType,
            Bank = data.Authorization.Bank,
            Last4 = data.Authorization.Last4,
            ExpMonth = data.Authorization.ExpMonth,
            ExpYear = data.Authorization.ExpYear,
            AccountName = data.Authorization.AccountName,
            CustomerEmail = data.Customer?.Email,
            ProviderCustomerCode = data.Customer?.CustomerCode,
            IsReusable = true,
            ConsentSource = CustomerMandateConsentSource.InstallationCheckout
        };

        try
        {
            var r = await _mandates.UpsertPaystackMandateAsync(request, cancellationToken);
            return r.IsSuccess ? "Reusable authorization stored." : $"Mandate store skipped: {r.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PaystackMandate] upsert threw for invoice {InvoiceId} reference {Reference}",
                initiation.InvoiceId, data.Reference);
            return "Mandate store failed (logged).";
        }
    }

    private static bool IsSignatureValid(string rawBody, string? headerValue, string secretKey)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        if (string.IsNullOrWhiteSpace(secretKey)) return false;

        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
        var expectedHex = Convert.ToHexString(hash);

        // Constant-time compare. Paystack docs use hex (lower-case);
        // accept either case so a future format change doesn't silently
        // break us.
        var headerBytes = Encoding.ASCII.GetBytes(headerValue.Trim());
        var expectedBytes = Encoding.ASCII.GetBytes(expectedHex);
        if (headerBytes.Length != expectedBytes.Length)
        {
            // Case-insensitive compare via ToUpperInvariant. Fixed-length
            // path — never short-circuits, so it stays constant-time.
            var upperHeader = Encoding.ASCII.GetBytes(headerValue.Trim().ToUpperInvariant());
            if (upperHeader.Length != expectedBytes.Length) return false;
            return CryptographicOperations.FixedTimeEquals(upperHeader, expectedBytes);
        }
        // Same length already — compare in-place (case-sensitive). Most
        // legitimate headers hit this path.
        return CryptographicOperations.FixedTimeEquals(headerBytes, expectedBytes);
    }

    // ZAR → kobo / cents. Same conversion the initiator uses; keep
    // local to avoid an Application → Infrastructure dependency.
    internal static long ToSubunits(decimal amount)
        => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    // ─── wire DTOs (private) ──────────────────────────────────────────

    private class PaystackEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")]  public PaystackEventData? Data { get; set; }
    }

    private class PaystackEventData
    {
        // Paystack's data.id is a JSON number; we keep it as long? to
        // match the wire and stringify only when persisting.
        [JsonPropertyName("id")]            public long? Id { get; set; }
        [JsonPropertyName("status")]        public string? Status { get; set; }
        [JsonPropertyName("reference")]     public string? Reference { get; set; }
        [JsonPropertyName("amount")]        public long Amount { get; set; }
        [JsonPropertyName("currency")]      public string? Currency { get; set; }
        [JsonPropertyName("paid_at")]       public DateTime? PaidAt { get; set; }
        [JsonPropertyName("authorization")] public PaystackEventAuthorization? Authorization { get; set; }
        [JsonPropertyName("customer")]      public PaystackEventCustomer? Customer { get; set; }
    }

    // Per Paystack charge.success docs. Reusable=true means we can
    // charge_authorization with the authorization_code later. We
    // deliberately do NOT model the full set of fields — only the
    // safe-to-store ones.
    private class PaystackEventAuthorization
    {
        [JsonPropertyName("authorization_code")] public string? AuthorizationCode { get; set; }
        [JsonPropertyName("bin")]                public string? Bin { get; set; }
        [JsonPropertyName("last4")]              public string? Last4 { get; set; }
        [JsonPropertyName("exp_month")]          public string? ExpMonth { get; set; }
        [JsonPropertyName("exp_year")]           public string? ExpYear { get; set; }
        [JsonPropertyName("channel")]            public string? Channel { get; set; }
        [JsonPropertyName("card_type")]          public string? CardType { get; set; }
        [JsonPropertyName("bank")]               public string? Bank { get; set; }
        [JsonPropertyName("country_code")]       public string? CountryCode { get; set; }
        [JsonPropertyName("brand")]              public string? Brand { get; set; }
        [JsonPropertyName("reusable")]           public bool?   Reusable { get; set; }
        [JsonPropertyName("signature")]          public string? Signature { get; set; }
        [JsonPropertyName("account_name")]       public string? AccountName { get; set; }
    }

    private class PaystackEventCustomer
    {
        [JsonPropertyName("id")]            public long?   Id { get; set; }
        [JsonPropertyName("first_name")]    public string? FirstName { get; set; }
        [JsonPropertyName("last_name")]     public string? LastName { get; set; }
        [JsonPropertyName("email")]         public string? Email { get; set; }
        [JsonPropertyName("customer_code")] public string? CustomerCode { get; set; }
    }
}

public class PaystackNotifyOutcome
{
    public bool   Accepted        { get; }
    public string Message         { get; }
    public Guid?  DiagnosticId    { get; }
    public string? Reference      { get; }

    public PaystackNotifyOutcome(bool accepted, string message, Guid? diagnosticId = null, string? reference = null)
    {
        Accepted = accepted;
        Message = message;
        DiagnosticId = diagnosticId;
        Reference = reference;
    }
}

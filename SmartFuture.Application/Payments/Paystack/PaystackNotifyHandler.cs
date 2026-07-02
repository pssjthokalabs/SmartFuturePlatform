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
            // ─── Phase 6 — decision-core delegated flow ─────────────
            //
            // The whole "which gate rejects, which no-ops, which
            // applies" decision lives in PaystackNotifyDecisionCore.
            // This wrapper's job is:
            //   1. Compute the input snapshot (signature check, JSON
            //      parse, reference lookup, apply-gate values).
            //   2. Call the core.
            //   3. Preserve the log / warn / info calls each branch
            //      used to emit inline — so operator telemetry stays
            //      byte-identical.
            //   4. Fire the side effects the outcome asks for
            //      (persist gateway id, mandate upsert, apply status
            //      change).

            var rawBodyProvided = !string.IsNullOrWhiteSpace(rawBody);

            // Signature check only when the pre-signature gates would
            // pass — matches the previous inline order. Otherwise the
            // core rejects on the earlier gate anyway.
            if (rawBodyProvided && _settings.Enabled && _settings.IsConfigured)
            {
                log.SignatureValid = IsSignatureValid(rawBody!, signatureHeader, _settings.SecretKey);
            }

            PaystackEvent? evt = null;
            var jsonParsed = false;
            var eventDataPresent = false;
            if (log.SignatureValid)
            {
                try
                {
                    evt = JsonSerializer.Deserialize<PaystackEvent>(rawBody!, JsonOptions);
                    jsonParsed = evt is not null;
                    eventDataPresent = jsonParsed && evt!.Data is not null;
                    if (eventDataPresent)
                    {
                        log.Event          = evt!.Event;
                        log.Reference      = evt.Data!.Reference;
                        log.AmountSubunits = evt.Data.Amount;
                        log.Currency       = evt.Data.Currency;
                        log.Status         = evt.Data.Status;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PaystackNotify] malformed JSON body");
                }
            }

            var reference = evt?.Data?.Reference;
            var isIntentRef = !string.IsNullOrWhiteSpace(reference)
                              && OrderIntents.OrderIntentService.IsIntentReference(reference);
            var eventIsChargeSuccess = eventDataPresent
                && string.Equals(evt?.Event, "charge.success", StringComparison.OrdinalIgnoreCase);

            // Load the invoice-bound PaymentInitiation for the non-intent
            // charge.success path. Any other outcome short-circuits before
            // needing this, so we skip the DB round-trip when the core is
            // going to reject on an earlier gate.
            Domain.Billing.PaymentInitiation? initiation = null;
            var paymentAlreadyCompleted = false;
            var expectedSubunits = 0L;
            var initiationApplyModeIsApplyNormally = true;
            if (!isIntentRef && !string.IsNullOrWhiteSpace(reference) && eventIsChargeSuccess)
            {
                initiation = await _dbContext.PaymentInitiations
                    .Include(i => i.Payment)
                    .Include(i => i.Invoice)
                    .FirstOrDefaultAsync(i => i.Provider == PaymentProviderType.Paystack
                                           && i.ProviderReference == reference,
                                         cancellationToken);
                if (initiation?.Payment is not null && initiation.Invoice is not null)
                {
                    log.PaymentInitiationId = initiation.Id;
                    log.PaymentId           = initiation.Payment.Id;
                    log.InvoiceId           = initiation.InvoiceId;
                    paymentAlreadyCompleted = initiation.Payment.Status == PaymentStatus.Completed;
                    expectedSubunits = ToSubunits(initiation.Payment.Amount);
                    initiationApplyModeIsApplyNormally = initiation.WebhookApplyMode == WebhookApplyMode.ApplyNormally;
                }
            }
            var paymentInitiationFound = initiation?.Payment is not null && initiation.Invoice is not null;

            var expectedCurrency = _settings.Currency ?? "ZAR";

            // ─── First decision-core call: everything up to verify ──
            var input1 = new PaystackNotifyDecisionInput
            {
                RawBodyProvided                    = rawBodyProvided,
                ProviderEnabled                    = _settings.Enabled,
                ProviderConfigured                 = _settings.IsConfigured,
                SignatureValid                     = log.SignatureValid,
                JsonParsed                         = jsonParsed,
                EventDataPresent                   = eventDataPresent,
                EventName                          = evt?.Event,
                Reference                          = reference,
                WebhookAmountSubunits              = evt?.Data?.Amount ?? 0,
                WebhookCurrency                    = evt?.Data?.Currency,
                WebhookStatus                      = evt?.Data?.Status,
                IsIntentReference                  = isIntentRef,
                PaymentInitiationFound             = paymentInitiationFound,
                PaymentAlreadyCompleted            = paymentAlreadyCompleted,
                ExpectedAmountSubunits             = expectedSubunits,
                ExpectedCurrency                   = expectedCurrency,
                VerifyGate                         = PaystackVerifyGateOutcome.NotChecked,
                WebhookApplyEnabled                = _processingSettings.WebhookApplyEnabled,
                InitiationApplyModeIsApplyNormally = initiationApplyModeIsApplyNormally,
            };
            var outcome1 = PaystackNotifyDecisionCore.Calculate(input1);

            // ─── Handle outcomes that don't need verify first ──────
            if (outcome1.ShouldReject)
            {
                LogRejectDetail(outcome1.RejectReason, evt, rawBody, expectedCurrency, expectedSubunits);
                return Reject(log, outcome1.RejectReason, RejectMessage(outcome1.RejectReason));
            }

            if (outcome1.ShouldConvertIntent)
            {
                // Delegate to the intent-conversion path — behaviour
                // preserved verbatim from the pre-Phase-6 handler.
                return await HandleIntentConvertAsync(evt!, log, cancellationToken);
            }

            if (outcome1.ShouldAcceptAsNoOp && !outcome1.ShouldPersistPaystackTransactionId)
            {
                // Terminal no-op: non-actionable event, already
                // completed, or status-not-success. No side effects.
                LogNoOpDetail(outcome1.NoOpReason, evt);
                return AcceptNoOp(log, outcome1.NoOpReason, NoOpMessage(outcome1.NoOpReason, evt));
            }

            // ─── outcome1 will lead to apply or apply-suppressed. Both need verify first. ─
            var paystackTransactionId = evt!.Data!.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var verifyGate = await ResolveVerifyGateAsync(
                evt.Data.Reference!, expectedSubunits, expectedCurrency, cancellationToken);

            // ─── Second decision-core call: post-verify decision ────
            var outcome2 = PaystackNotifyDecisionCore.Calculate(input1 with { VerifyGate = verifyGate });

            if (outcome2.ShouldReject)
            {
                // Only reject code that appears here is verify-disagreement.
                return Reject(log, outcome2.RejectReason, RejectMessage(outcome2.RejectReason));
            }

            // ─── Persist Paystack transaction id + save ─────────────
            if (outcome2.ShouldPersistPaystackTransactionId)
            {
                if (!string.IsNullOrWhiteSpace(paystackTransactionId))
                {
                    initiation!.Payment!.GatewayTransactionId = paystackTransactionId;
                    initiation.ProviderCheckoutId = paystackTransactionId;
                }
                initiation!.WebhookLastReceivedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            // ─── Mandate upsert (best-effort) ───────────────────────
            string? mandateUpsertedNote = null;
            if (outcome2.ShouldAttemptMandateUpsert)
            {
                mandateUpsertedNote = await TryUpsertPaystackMandateAsync(initiation!, evt.Data, cancellationToken);
            }

            // ─── Apply-suppressed no-op branch ──────────────────────
            if (outcome2.ShouldAcceptAsNoOp)
            {
                var suppressReason = outcome2.NoOpReason == PaystackNotifyDecisionCore.NoOpCodes.ApplySuppressedByWebhookApplyEnabled
                    ? "PaymentProcessing.WebhookApplyEnabled=false"
                    : $"PaymentInitiation.WebhookApplyMode={initiation!.WebhookApplyMode}";
                _logger.LogInformation(
                    "[PaystackWebhookDryRun] validated=true reference={Reference} applicationSuppressed=true reason='{Reason}' mandate='{Mandate}'",
                    evt.Data.Reference, suppressReason, mandateUpsertedNote ?? "(none)");
                return AcceptNoOp(log, $"apply-suppressed:{suppressReason}",
                    $"Validated; application suppressed ({suppressReason}).{(mandateUpsertedNote is null ? "" : $" {mandateUpsertedNote}")}");
            }

            // ─── Apply the status change (real path) ────────────────
            log.ApplyAttempted = true;
            var statusBefore = initiation!.Invoice!.Status.ToString();
            var result = await _applier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
            {
                PaymentId = initiation.Payment!.Id,
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

    // ─── Phase-6 wrapper helpers ───────────────────────────────────

    // Map a reject code from the decision core to the human message the
    // handler used to hard-code when it rejected inline. Preserves the
    // OutcomeMessage byte-for-byte so log consumers don't drift.
    private static string RejectMessage(string rejectCode) => rejectCode switch
    {
        PaystackNotifyDecisionCore.RejectCodes.EmptyBody               => "Empty body",
        PaystackNotifyDecisionCore.RejectCodes.PaystackNotEnabled      => "Paystack not enabled",
        PaystackNotifyDecisionCore.RejectCodes.PaystackNotConfigured   => "Paystack not configured",
        PaystackNotifyDecisionCore.RejectCodes.SignatureMismatch       => "Signature mismatch",
        PaystackNotifyDecisionCore.RejectCodes.MalformedJson           => "Malformed JSON",
        PaystackNotifyDecisionCore.RejectCodes.MissingEventOrData      => "Missing event/data",
        PaystackNotifyDecisionCore.RejectCodes.MissingReference        => "Missing reference",
        PaystackNotifyDecisionCore.RejectCodes.UnknownReference        => "Unknown reference",
        PaystackNotifyDecisionCore.RejectCodes.CurrencyMismatch        => "Currency mismatch",
        PaystackNotifyDecisionCore.RejectCodes.AmountMismatch          => "Amount mismatch",
        PaystackNotifyDecisionCore.RejectCodes.VerifyDisagreement      => "Verify disagreement",
        _                                                              => rejectCode,
    };

    // Map a no-op code from the decision core to the outcome message
    // the handler used to build inline. Preserves the OutcomeMessage
    // for the three "final" no-op paths (non-actionable, already-
    // completed, status-not-success). The apply-suppressed no-op has
    // its own custom message built at the callsite because it embeds
    // the mandate-upsert note.
    private static string NoOpMessage(string noOpCode, PaystackEvent? evt) => noOpCode switch
    {
        PaystackNotifyDecisionCore.NoOpCodes.NonActionableEvent
            => $"Event '{evt?.Event}' acknowledged (no state change).",
        PaystackNotifyDecisionCore.NoOpCodes.AlreadyCompleted
            => "Payment already Completed.",
        PaystackNotifyDecisionCore.NoOpCodes.StatusNotSuccess
            => $"Event status '{evt?.Data?.Status}' recorded; no state change.",
        _   => noOpCode,
    };

    // Emit the structured LogWarning / LogInformation each reject code
    // used to emit before its inline Reject/AcceptNoOp call. Preserves
    // operator-facing telemetry byte-for-byte.
    private void LogRejectDetail(string rejectCode, PaystackEvent? evt, string? rawBody, string expectedCurrency, long expectedSubunits)
    {
        switch (rejectCode)
        {
            case PaystackNotifyDecisionCore.RejectCodes.EmptyBody:
                _logger.LogWarning("[PaystackWebhookRejected] reason=empty-body");
                break;
            case PaystackNotifyDecisionCore.RejectCodes.PaystackNotEnabled:
                _logger.LogWarning("[PaystackWebhookRejected] reason=paystack-not-enabled");
                break;
            case PaystackNotifyDecisionCore.RejectCodes.PaystackNotConfigured:
                _logger.LogWarning("[PaystackWebhookRejected] reason=paystack-not-configured");
                break;
            case PaystackNotifyDecisionCore.RejectCodes.SignatureMismatch:
                _logger.LogWarning(
                    "[PaystackWebhookRejected] reason=signature-mismatch keyPrefix={KeyPrefix} bodyBytes={BodyBytes}",
                    ResolveSecretKeyDiagnosticPrefix(_settings.SecretKey), rawBody?.Length ?? 0);
                break;
            case PaystackNotifyDecisionCore.RejectCodes.UnknownReference:
                _logger.LogWarning(
                    "[PaystackWebhookRejected] reason=unknown-reference reference={Reference}",
                    evt?.Data?.Reference);
                break;
            case PaystackNotifyDecisionCore.RejectCodes.CurrencyMismatch:
                _logger.LogWarning(
                    "[PaystackWebhookRejected] reason=currency-mismatch reference={Reference} expected={Expected} got={Got}",
                    evt?.Data?.Reference,
                    (expectedCurrency ?? "ZAR").Trim().ToUpperInvariant(),
                    (evt?.Data?.Currency ?? string.Empty).Trim().ToUpperInvariant());
                break;
            case PaystackNotifyDecisionCore.RejectCodes.AmountMismatch:
                _logger.LogWarning(
                    "[PaystackWebhookRejected] reason=amount-mismatch reference={Reference} expectedSubunits={Expected} gotSubunits={Got}",
                    evt?.Data?.Reference, expectedSubunits, evt?.Data?.Amount);
                break;
            case PaystackNotifyDecisionCore.RejectCodes.MalformedJson:
            case PaystackNotifyDecisionCore.RejectCodes.MissingEventOrData:
            case PaystackNotifyDecisionCore.RejectCodes.MissingReference:
                // These matched the previous inline behaviour: no
                // dedicated LogWarning (the malformed-json path already
                // logged the exception inside the parse try/catch).
                break;
        }
    }

    private void LogNoOpDetail(string noOpCode, PaystackEvent? evt)
    {
        switch (noOpCode)
        {
            case PaystackNotifyDecisionCore.NoOpCodes.NonActionableEvent:
                _logger.LogInformation(
                    "[PaystackNotify] non-actionable event '{Event}' reference='{Reference}' — acknowledged without state change",
                    evt?.Event, evt?.Data?.Reference);
                break;
            case PaystackNotifyDecisionCore.NoOpCodes.AlreadyCompleted:
                // The original message referenced initiation.Payment.PaymentNumber;
                // we don't want to re-fetch here just for the log, and the
                // Reference-scoped variant preserves the same operator signal.
                _logger.LogInformation(
                    "[PaystackNotify] payment for reference '{Reference}' already Completed — webhook acknowledged without re-applying",
                    evt?.Data?.Reference);
                break;
            case PaystackNotifyDecisionCore.NoOpCodes.StatusNotSuccess:
                _logger.LogInformation(
                    "[PaystackNotify] event status '{Status}' for {Reference} — not applying Completed",
                    evt?.Data?.Status, evt?.Data?.Reference);
                break;
        }
    }

    // Run the belt-and-braces /transaction/verify call + map the result
    // into the decision core's PaystackVerifyGateOutcome enum. Preserves
    // the two log messages the inline verify branch used to emit.
    private async Task<PaystackVerifyGateOutcome> ResolveVerifyGateAsync(
        string reference, long expectedSubunits, string expectedCurrency, CancellationToken cancellationToken)
    {
        var verifyResult = await _verifier.VerifyAsync(reference, cancellationToken);
        if (!verifyResult.IsSuccess)
        {
            _logger.LogWarning(
                "[PaystackNotify] verify call failed for {Reference}: {Message} — proceeding on signed webhook only",
                reference, verifyResult.Message);
            return PaystackVerifyGateOutcome.CallFailed;
        }
        var v = verifyResult.Data!;
        if (!string.Equals(v.Status, "success", StringComparison.OrdinalIgnoreCase)
            || v.AmountSubunits != expectedSubunits
            || (!string.IsNullOrWhiteSpace(v.Currency)
                && !string.Equals(v.Currency, expectedCurrency, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(
                "[PaystackNotify] verify-call disagreement for {Reference}: verifyStatus={Status} verifyAmount={Amount} verifyCurrency={Currency}",
                reference, v.Status, v.AmountSubunits, v.Currency);
            return PaystackVerifyGateOutcome.Disagreement;
        }
        return PaystackVerifyGateOutcome.AgreesWithWebhook;
    }

    // Delegate to the Phase 53 SF-INTENT- convert path. Behaviour
    // preserved verbatim from the pre-Phase-6 handler.
    private async Task<PaystackNotifyOutcome> HandleIntentConvertAsync(
        PaystackEvent evt, PaystackWebhookLog log, CancellationToken cancellationToken)
    {
        // Pass the webhook's authorization block straight into the
        // convert path so the same reusable authorization that would
        // feed TryUpsertPaystackMandateAsync on the invoice path also
        // gets stored for the OrderIntent flow.
        PaystackVerifyAuthorizationSnapshot? webhookAuth = null;
        if (evt.Data!.Authorization is not null
            && !string.IsNullOrWhiteSpace(evt.Data.Authorization.AuthorizationCode))
        {
            webhookAuth = new PaystackVerifyAuthorizationSnapshot(
                AuthorizationCode:    evt.Data.Authorization.AuthorizationCode!,
                Reusable:             evt.Data.Authorization.Reusable ?? false,
                Signature:            evt.Data.Authorization.Signature,
                Channel:              evt.Data.Authorization.Channel,
                CardType:             evt.Data.Authorization.CardType,
                Bank:                 evt.Data.Authorization.Bank,
                Last4:                evt.Data.Authorization.Last4,
                ExpMonth:             evt.Data.Authorization.ExpMonth,
                ExpYear:              evt.Data.Authorization.ExpYear,
                AccountName:          evt.Data.Authorization.AccountName,
                ProviderCustomerCode: evt.Data.Customer?.CustomerCode);
        }

        var convert = await _orderIntentService.ConvertIntentPaymentToPaidOrderAsync(
            evt.Data.Reference!, evt.Data.PaidAt ?? DateTime.UtcNow,
            evt.Data.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            webhookAuth,
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

        log.ApplyAttempted    = true;
        log.ApplyErrorCode    = convert.Code;
        log.ApplyErrorMessage = convert.Message;
        _logger.LogError(
            "[PaystackWebhookApply] intent-convert failed reference={Reference} code={Code} message='{Message}'",
            evt.Data.Reference, convert.Code, convert.Message);
        return Reject(log, "intent-convert-failed", convert.Message ?? "Intent convert failed.");
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
            ConsentSource = CustomerMandateConsentSource.InstallationCheckout,
            // Go-live: order/installation-fee payments enable
            // AutoBillingEnabled so the first monthly invoice can be
            // auto-debited without a separate opt-in.
            AutoEnableAutoBilling = true,
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
        => PaystackWebhookMapper.IsWebhookSignatureValid(rawBody, headerValue, secretKey);

    // ZAR → kobo / cents. Delegates to the shared mapper so tests can
    // verify the same conversion the notify handler + initiator both use.
    internal static long ToSubunits(decimal amount)
        => PaystackWebhookMapper.ToSubunits(amount);

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

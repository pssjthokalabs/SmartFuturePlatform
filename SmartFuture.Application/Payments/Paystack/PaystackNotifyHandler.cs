using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Persistence;
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
    private readonly ILogger<PaystackNotifyHandler> _logger;

    public PaystackNotifyHandler(
        IAppDbContext dbContext,
        IPaymentApplierService applier,
        IOptions<PaystackSettings> settings,
        IOptions<PaymentProcessingSettings> processingSettings,
        PaystackVerificationService verifier,
        ICustomerPaymentMandateService mandates,
        ILogger<PaystackNotifyHandler> logger)
    {
        _dbContext = dbContext;
        _applier = applier;
        _settings = settings.Value;
        _processingSettings = processingSettings.Value;
        _verifier = verifier;
        _mandates = mandates;
        _logger = logger;
    }

    /// <summary>
    /// Validate + apply. Pass the **raw** JSON request body and the
    /// <c>x-paystack-signature</c> header value. Always returns
    /// <see cref="PaystackNotifyOutcome"/>; the controller wraps that
    /// in an HTTP 200.
    /// </summary>
    public async Task<PaystackNotifyOutcome> HandleAsync(string rawBody, string? signatureHeader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return new PaystackNotifyOutcome(false, "Empty body");

        if (!_settings.Enabled)
        {
            _logger.LogWarning("Received Paystack notify but Paystack is not enabled — ignoring.");
            return new PaystackNotifyOutcome(false, "Paystack not enabled");
        }

        if (!_settings.IsConfigured)
        {
            _logger.LogWarning("Received Paystack notify but Paystack is not configured — ignoring.");
            return new PaystackNotifyOutcome(false, "Paystack not configured");
        }

        // 1) Signature — HMAC-SHA512(rawBody, secretKey), hex-lowercase,
        //    constant-time compare.
        if (!IsSignatureValid(rawBody, signatureHeader, _settings.SecretKey))
        {
            _logger.LogWarning("[PaystackNotify] signature mismatch — rejecting webhook");
            return new PaystackNotifyOutcome(false, "Signature mismatch");
        }

        PaystackEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<PaystackEvent>(rawBody, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PaystackNotify] malformed JSON body");
            return new PaystackNotifyOutcome(false, "Malformed JSON");
        }

        if (evt is null || evt.Data is null)
            return new PaystackNotifyOutcome(false, "Missing event/data");

        // 2) Event filter — we only act on charge.success today. Other
        //    events return 200 to stop Paystack retrying but don't
        //    mutate any payment state.
        if (!string.Equals(evt.Event, "charge.success", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "[PaystackNotify] non-actionable event '{Event}' reference='{Reference}' — acknowledged without state change",
                evt.Event, evt.Data.Reference);
            return new PaystackNotifyOutcome(true, $"Event '{evt.Event}' acknowledged (no state change).");
        }

        if (string.IsNullOrWhiteSpace(evt.Data.Reference))
            return new PaystackNotifyOutcome(false, "Missing reference");

        // 3) Find the matching PaymentInitiation we created at initiate.
        var initiation = await _dbContext.PaymentInitiations
            .Include(i => i.Payment)
            .Include(i => i.Invoice)
            .FirstOrDefaultAsync(i => i.Provider == PaymentProviderType.Paystack
                                   && i.ProviderReference == evt.Data.Reference,
                                 cancellationToken);

        if (initiation is null || initiation.Payment is null || initiation.Invoice is null)
        {
            _logger.LogWarning("[PaystackNotify] unknown reference {Reference}", evt.Data.Reference);
            return new PaystackNotifyOutcome(false, "Unknown reference");
        }

        // 4) Idempotency — already-Completed payment is a no-op.
        if (initiation.Payment.Status == PaymentStatus.Completed)
        {
            _logger.LogInformation(
                "[PaystackNotify] payment {PaymentNumber} already Completed — webhook acknowledged without re-applying",
                initiation.Payment.PaymentNumber);
            return new PaystackNotifyOutcome(true, "Payment already Completed.");
        }

        // 5) Cross-checks: currency + amount + transaction status.
        var webhookCurrency = (evt.Data.Currency ?? string.Empty).Trim().ToUpperInvariant();
        var expectedCurrency = (_settings.Currency ?? "ZAR").Trim().ToUpperInvariant();
        if (!string.Equals(webhookCurrency, expectedCurrency, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "[PaystackNotify] currency mismatch for {Reference}: expected {Expected} got {Got}",
                evt.Data.Reference, expectedCurrency, webhookCurrency);
            return new PaystackNotifyOutcome(false, "Currency mismatch");
        }

        var expectedSubunits = ToSubunits(initiation.Payment.Amount);
        if (evt.Data.Amount != expectedSubunits)
        {
            _logger.LogWarning(
                "[PaystackNotify] amount mismatch for {Reference}: expected {Expected} subunits, got {Got}",
                evt.Data.Reference, expectedSubunits, evt.Data.Amount);
            return new PaystackNotifyOutcome(false, "Amount mismatch");
        }

        if (!string.Equals(evt.Data.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "[PaystackNotify] event status '{Status}' for {Reference} — not applying Completed",
                evt.Data.Status, evt.Data.Reference);
            return new PaystackNotifyOutcome(true, $"Event status '{evt.Data.Status}' recorded; no state change.");
        }

        // 6) Optional belt-and-braces verify call. We treat a 5xx /
        //    transport failure as a soft skip — the signature already
        //    proved authenticity, the amount and currency already
        //    matched. Hard mismatches (different amount, different
        //    currency, status != success) DO abort.
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
                return new PaystackNotifyOutcome(false, "Verify disagreement");
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

        // 8) Phase 2 — store reusable authorization (when not dry-run).
        //    Done BEFORE the apply step so a mandate-upsert failure
        //    doesn't block the customer's invoice from flipping to Paid.
        //    Mandate-upsert errors are logged but never abort the apply.
        var mandateUpsertedNote = await TryUpsertPaystackMandateAsync(initiation, evt.Data, cancellationToken);

        // 9) Phase 1 — dry-run / kill-switch gating.
        //    Master flag wins; per-initiation flag is the secondary gate.
        //    Authority is OUR DB row, never the inbound metadata.
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
            return new PaystackNotifyOutcome(true,
                $"Validated; application suppressed ({reason}).{(mandateUpsertedNote is null ? "" : $" {mandateUpsertedNote}")}");
        }

        // 10) Apply Completed via PaymentApplierService (itself idempotent).
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
            _logger.LogError(
                "[PaystackNotify] ApplyStatusChangeAsync failed for {Reference}: {Code} {Message}",
                evt.Data.Reference, result.Code, result.Message);
            return new PaystackNotifyOutcome(false, result.Message ?? "Apply failed");
        }

        return new PaystackNotifyOutcome(true,
            $"Payment {initiation.Payment.PaymentNumber} updated to Completed.{(mandateUpsertedNote is null ? "" : $" {mandateUpsertedNote}")}");
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
    public bool   Accepted { get; }
    public string Message  { get; }
    public PaystackNotifyOutcome(bool accepted, string message)
    {
        Accepted = accepted;
        Message = message;
    }
}

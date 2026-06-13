using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Webhooks;
using SmartFuture.Shared.Enums.Webhooks;

namespace SmartFuture.Application.Payments.PayFast;

public class PayFastWebhookBridge : IPayFastWebhookBridge
{
    private const int MaxReferenceLength = 200;
    private const int MaxFailureReasonLength = 2000;
    private const int MaxIdempotencyKeyLength = 300;
    private const int MaxProviderEventIdLength = 200;
    private const int MaxSignatureHeaderLength = 500;

    private readonly IAppDbContext _dbContext;
    private readonly PayFastNotifyHandler _notifyHandler;
    private readonly ILogger<PayFastWebhookBridge> _logger;

    public PayFastWebhookBridge(
        IAppDbContext dbContext,
        PayFastNotifyHandler notifyHandler,
        ILogger<PayFastWebhookBridge> logger)
    {
        _dbContext = dbContext;
        _notifyHandler = notifyHandler;
        _logger = logger;
    }

    public async Task<PayFastWebhookBridgeOutcome> HandleAsync(
        string rawFormBody,
        IReadOnlyDictionary<string, string>? preParsedFields,
        string? signatureHeader,
        string? providerEventIdHeader,
        string? idempotencyKeyHeader,
        CancellationToken cancellationToken = default)
    {
        rawFormBody ??= string.Empty;

        // Prefer the pre-parsed form fields from the controller — they
        // come from Request.ReadFormAsync which knows how to handle
        // form bodies safely. Parsing rawFormBody ourselves is a
        // fallback for non-form content types (rare PayFast edge
        // cases) or for ad-hoc callers / unit tests.
        Dictionary<string, string> fields;
        var fieldsSource = "rawBody";
        if (preParsedFields is not null && preParsedFields.Count > 0)
        {
            fields = new Dictionary<string, string>(preParsedFields, StringComparer.OrdinalIgnoreCase);
            fieldsSource = "preParsed";
        }
        else
        {
            fields = ParseFormBody(rawFormBody);
        }

        // Recompute a canonical raw body for hashing when we used the
        // pre-parsed fields. This makes the SHA-256 stable across
        // duplicate ITN attempts even when the wire encoding differs
        // (e.g. PayFast vs PowerShell), and never produces the empty
        // SHA-256 (E3B0C44…) when the fields are present.
        var canonicalBody = fieldsSource == "preParsed"
            ? BuildCanonicalBody(fields)
            : rawFormBody;
        var rawHash = ComputeSha256(canonicalBody);

        // ENTRY LOG — proves the controller invoked the bridge. If
        // [payment][webhook][hit] is present but this is missing, DI
        // wiring is broken or the controller branch didn't match
        // providerName=payfast.
        _logger.LogInformation(
            "[payment][payfast][bridge_hit] rawLength={RawLength} hasPayload={HasPayload} hasSignatureHeader={HasSigHeader} fieldsSource={FieldsSource} fieldsCount={FieldsCount}",
            rawFormBody.Length,
            rawFormBody.Length > 0,
            !string.IsNullOrEmpty(signatureHeader),
            fieldsSource,
            fields.Count);

        var payload = BuildPayload(fields);

        // PARSED LOG — proves the form body parsed correctly and shows
        // the safe (non-secret) fields. Passphrase, signature, and
        // merchant_key are NEVER logged.
        _logger.LogInformation(
            "[payment][payfast][itn_parsed] reference={Reference} pfPaymentId={PfPaymentId} status={Status} amountGross={AmountGross} amountFee={AmountFee} amountNet={AmountNet} merchantId={MerchantId} hasSignature={HasSignature} fieldsCount={FieldsCount}",
            payload.MPaymentId, payload.PfPaymentId, payload.PaymentStatus,
            payload.AmountGross, payload.AmountFee, payload.AmountNet,
            payload.MerchantId, !string.IsNullOrEmpty(payload.Signature), fields.Count);

        // PayFast does not send a per-event id, so we derive idempotency
        // from the (m_payment_id, pf_payment_id, payment_status) tuple
        // when the caller didn't supply an x-provider-event-id header.
        //
        // CRITICAL: when required fields are missing (malformed or
        // empty payload), BuildSyntheticEventId returns a
        // unique-per-request id so we DO NOT collapse every bad row
        // into the same dedupe key. The previous behaviour produced
        // `payfast:(no-ref):(no-pf):(no-status)` for every malformed
        // request and hid future diagnostics behind the first such
        // row (2026-06 UAT incident).
        var effectiveEventId = !string.IsNullOrWhiteSpace(providerEventIdHeader)
            ? providerEventIdHeader
            : BuildSyntheticEventId(payload, rawHash);

        _logger.LogInformation(
            "[payment][payfast][event_id] eventId={EventId} source={Source} eventIdHeaderPresent={HeaderPresent}",
            effectiveEventId,
            !string.IsNullOrWhiteSpace(providerEventIdHeader) ? "header" : (IsMalformedPayload(payload) ? "malformed-unique" : "synthetic-tuple"),
            !string.IsNullOrWhiteSpace(providerEventIdHeader));

        var duplicate = await FindDuplicateAsync(effectiveEventId, idempotencyKeyHeader, cancellationToken);
        if (duplicate is not null)
        {
            _logger.LogInformation(
                "[PayFastBridge] Duplicate PayFast ITN received — original inbox id {InboxId} for reference {Reference}",
                duplicate.Id, payload.MPaymentId);

            return new PayFastWebhookBridgeOutcome
            {
                Accepted = true,
                Message = $"Duplicate PayFast ITN; original inbox {duplicate.Id}.",
                InboxId = duplicate.Id,
            };
        }

        // Persist Received row up front so /admin/inbox sees the receipt
        // even if the notify handler throws.
        var inbox = new WebhookInbox
        {
            Provider = WebhookProvider.PayFast,
            ProviderName = "PayFast",
            ProviderEventId = Truncate(effectiveEventId, MaxProviderEventIdLength),
            EventType = MapEventType(payload.PaymentStatus),
            Status = WebhookInboxStatus.Received,
            RawPayloadHash = rawHash,
            SignatureHeader = Truncate(signatureHeader, MaxSignatureHeaderLength),
            IdempotencyKey = Truncate(idempotencyKeyHeader, MaxIdempotencyKeyLength),
            PaymentReference = Truncate(payload.MPaymentId, MaxReferenceLength),
            GatewayTransactionId = Truncate(payload.PfPaymentId, MaxReferenceLength),
            Amount = payload.AmountGross > 0 ? payload.AmountGross : null,
            CurrencyCode = "ZAR",
            ProviderCreatedAtUtc = DateTime.UtcNow,
            ParsedSummaryJson = BuildSummaryJson(payload),
        };
        _dbContext.WebhookInboxes.Add(inbox);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[PayFastBridge] PayFast ITN received — reference={Reference} pf_payment_id={PfPaymentId} status={Status} amount={Amount} inbox={InboxId}",
            payload.MPaymentId, payload.PfPaymentId, payload.PaymentStatus, payload.AmountGross, inbox.Id);

        // Dispatch to the existing handler. It validates signature,
        // merchant id, amount, and (on COMPLETE) calls
        // ConvertIntentPaymentToPaidOrderAsync atomically.
        PayFastNotifyOutcome outcome;
        try
        {
            outcome = await _notifyHandler.HandleAsync(payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PayFastBridge] PayFastNotifyHandler threw for reference {Reference} (inbox {InboxId})",
                payload.MPaymentId, inbox.Id);

            inbox.Status = WebhookInboxStatus.Failed;
            inbox.FailureReason = Truncate($"Notify handler threw: {ex.Message}", MaxFailureReasonLength);
            inbox.ProcessedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new PayFastWebhookBridgeOutcome
            {
                Accepted = false,
                Message = "Notify handler exception (logged).",
                InboxId = inbox.Id,
            };
        }

        if (outcome.Accepted)
        {
            inbox.Status = WebhookInboxStatus.Processed;
            inbox.FailureReason = Truncate(outcome.Message, MaxFailureReasonLength);
        }
        else
        {
            inbox.Status = IsSignatureRelated(outcome.Message)
                ? WebhookInboxStatus.SignatureInvalid
                : WebhookInboxStatus.Failed;
            inbox.FailureReason = Truncate(outcome.Message, MaxFailureReasonLength);
        }
        inbox.ProcessedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new PayFastWebhookBridgeOutcome
        {
            Accepted = outcome.Accepted,
            Message = outcome.Message,
            InboxId = inbox.Id,
        };
    }

    private async Task<WebhookInbox?> FindDuplicateAsync(string? effectiveEventId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(effectiveEventId))
        {
            var byEvent = await _dbContext.WebhookInboxes
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Provider == WebhookProvider.PayFast
                                       && w.ProviderEventId == effectiveEventId,
                                     cancellationToken);
            if (byEvent is not null) return byEvent;
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var byKey = await _dbContext.WebhookInboxes
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Provider == WebhookProvider.PayFast
                                       && w.IdempotencyKey == idempotencyKey,
                                     cancellationToken);
            if (byKey is not null) return byKey;
        }

        return null;
    }

    private static Dictionary<string, string> ParseFormBody(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return result;

        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                result[HttpUtility.UrlDecode(pair)] = string.Empty;
                continue;
            }
            var key = HttpUtility.UrlDecode(pair[..eq]);
            var value = HttpUtility.UrlDecode(pair[(eq + 1)..]);
            if (!string.IsNullOrEmpty(key)) result[key] = value;
        }
        return result;
    }

    private static PayFastNotifyPayload BuildPayload(Dictionary<string, string> f)
    {
        return new PayFastNotifyPayload
        {
            MPaymentId      = Get(f, "m_payment_id"),
            PfPaymentId     = Get(f, "pf_payment_id"),
            PaymentStatus   = Get(f, "payment_status"),
            ItemName        = Get(f, "item_name"),
            ItemDescription = Get(f, "item_description"),
            AmountGross     = ParseDecimal(Get(f, "amount_gross")),
            AmountFee       = ParseDecimal(Get(f, "amount_fee")),
            AmountNet       = ParseDecimal(Get(f, "amount_net")),
            CustomStr1      = Get(f, "custom_str1"),
            CustomStr2      = Get(f, "custom_str2"),
            CustomStr3      = Get(f, "custom_str3"),
            CustomStr4      = Get(f, "custom_str4"),
            CustomStr5      = Get(f, "custom_str5"),
            CustomInt1      = ParseInt(Get(f, "custom_int1")),
            CustomInt2      = ParseInt(Get(f, "custom_int2")),
            CustomInt3      = ParseInt(Get(f, "custom_int3")),
            CustomInt4      = ParseInt(Get(f, "custom_int4")),
            CustomInt5      = ParseInt(Get(f, "custom_int5")),
            NameFirst       = Get(f, "name_first"),
            NameLast        = Get(f, "name_last"),
            EmailAddress    = Get(f, "email_address"),
            MerchantId      = Get(f, "merchant_id"),
            Signature       = Get(f, "signature"),
        };
    }

    private static string? Get(Dictionary<string, string> f, string key) =>
        f.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private static decimal ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private static WebhookEventType MapEventType(string? payFastStatus) => payFastStatus?.Trim().ToUpperInvariant() switch
    {
        "COMPLETE" => WebhookEventType.PaymentCompleted,
        "FAILED"   => WebhookEventType.PaymentFailed,
        "CANCELLED" or "CANCELED" => WebhookEventType.PaymentCancelled,
        "PENDING"  => WebhookEventType.PaymentPending,
        _ => WebhookEventType.Unknown,
    };

    /// <summary>
    /// SHA-256 hex of the empty string, returned by .NET's SHA-256.
    /// Used to detect when an empty raw body was hashed — see
    /// <see cref="BuildSyntheticEventId"/>.
    /// </summary>
    private const string EmptyStringSha256 =
        "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

    private static bool IsMalformedPayload(PayFastNotifyPayload p) =>
        string.IsNullOrWhiteSpace(p.MPaymentId)
        && string.IsNullOrWhiteSpace(p.PfPaymentId)
        && string.IsNullOrWhiteSpace(p.PaymentStatus);

    private static string BuildSyntheticEventId(PayFastNotifyPayload p, string rawHash)
    {
        // Well-formed: stable tuple so genuine PayFast retries dedupe.
        if (!string.IsNullOrWhiteSpace(p.MPaymentId)
            && !string.IsNullOrWhiteSpace(p.PfPaymentId)
            && !string.IsNullOrWhiteSpace(p.PaymentStatus))
        {
            return $"payfast:{p.MPaymentId}:{p.PfPaymentId}:{p.PaymentStatus}";
        }

        // Partial (one or two of the three identifying fields are
        // present). Use whatever we have, plus a hash slice so two
        // partial payloads with different other-fields don't collapse.
        var refPart = p.MPaymentId ?? "(no-ref)";
        var pfPart = p.PfPaymentId ?? "(no-pf)";
        var statusPart = p.PaymentStatus ?? "(no-status)";

        // Malformed / empty: never collapse. We append a unique-per
        // -request disambiguator so each bad payload writes its own
        // row and admin diagnostics aren't hidden behind the first
        // one ever received.
        var disambiguator = string.IsNullOrEmpty(rawHash) || rawHash == EmptyStringSha256
            ? $"empty:{Guid.NewGuid():N}"
            : $"hash:{rawHash[..Math.Min(16, rawHash.Length)]}:{Guid.NewGuid():N}";

        return $"payfast:malformed:{refPart}:{pfPart}:{statusPart}:{disambiguator}";
    }

    private static string BuildCanonicalBody(Dictionary<string, string> fields)
    {
        // Canonical form for hashing: keys sorted, URL-encoded.
        // Two ITNs with the same fields produce identical hashes
        // regardless of wire-encoding (PayFast vs PowerShell). We
        // hash this — never the raw bytes — because PayFast can
        // re-order or re-encode fields between retries.
        if (fields.Count == 0) return string.Empty;
        var ordered = fields
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value ?? string.Empty)}");
        return string.Join("&", ordered);
    }

    private static bool IsSignatureRelated(string? message) =>
        !string.IsNullOrEmpty(message)
        && (message.Contains("signature", StringComparison.OrdinalIgnoreCase)
         || message.Contains("merchant id", StringComparison.OrdinalIgnoreCase)
         || message.Contains("Merchant ID", StringComparison.Ordinal));

    private static string ComputeSha256(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }

    private static string BuildSummaryJson(PayFastNotifyPayload p)
    {
        // Safe summary — no merchant credentials, signature, or card data
        // (PayFast ITN never carries the card PAN; only public fields are
        // captured here so /admin/inbox can show useful diagnostics).
        try
        {
            return JsonSerializer.Serialize(new
            {
                mPaymentId = p.MPaymentId,
                pfPaymentId = p.PfPaymentId,
                paymentStatus = p.PaymentStatus,
                amountGross = p.AmountGross,
                amountFee = p.AmountFee,
                amountNet = p.AmountNet,
                merchantId = p.MerchantId,
                emailAddress = p.EmailAddress,
                nameFirst = p.NameFirst,
                nameLast = p.NameLast,
            });
        }
        catch
        {
            return "{}";
        }
    }
}

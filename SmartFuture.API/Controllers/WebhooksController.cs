using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartFuture.API.Extensions;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.PayFast.Diagnostics;
using SmartFuture.Application.Webhooks;
using SmartFuture.Application.Webhooks.Dtos;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Webhooks;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

[Route("api/webhooks")]
public class WebhooksController : BaseController
{
    private static readonly string[] SignatureHeaderCandidates =
    {
        "x-signature",
        "x-payfast-signature",
        "x-peach-signature",
        "x-yoco-signature",
        "x-paystack-signature",
        "x-ozow-signature"
    };

    private const string IdempotencyHeader = "x-idempotency-key";
    private const string ProviderEventIdHeader = "x-provider-event-id";

    private readonly IWebhookInboxService _service;
    private readonly IPayFastWebhookBridge _payFastBridge;
    private readonly IPayFastWebhookForensicCapture _forensicCapture;
    private readonly ILogger<WebhooksController> _logger;

    // TEMPORARY UAT FORENSIC DIAGNOSTICS — header redaction lists.
    // Remove or hard-gate to non-Production before going live.
    private static readonly HashSet<string> AlwaysRedactHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Set-Cookie"
    };
    private static readonly string[] SubstringRedactPatterns =
    {
        "secret", "key", "token", "password"
    };

    public WebhooksController(
        IWebhookInboxService service,
        IPayFastWebhookBridge payFastBridge,
        IPayFastWebhookForensicCapture forensicCapture,
        ILogger<WebhooksController> logger)
    {
        _service = service;
        _payFastBridge = payFastBridge;
        _forensicCapture = forensicCapture;
        _logger = logger;
    }

    [HttpPost("payments/{providerName}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.WebhookPolicy)]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> ReceivePayment(string providerName, CancellationToken cancellationToken)
    {
        // FIRST-LINE LOG — proves the request reached the controller
        // BEFORE any validation, body read, or DI side-effects could
        // throw. If this log is missing for a given ITN delivery,
        // PayFast did NOT reach this URL (DNS, firewall, wrong env var,
        // wrong host, TLS cert issue). Always logged at Information.
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "(unknown)";
        var userAgent = Request.Headers.UserAgent.ToString();
        _logger.LogInformation(
            "[payment][webhook][hit] provider={Provider} method={Method} path={Path} contentType={ContentType} contentLength={ContentLength} hasFormContentType={HasFormContentType} remoteIp={RemoteIp} userAgent={UserAgent}",
            providerName, Request.Method, Request.Path.Value, Request.ContentType,
            Request.ContentLength, Request.HasFormContentType, remoteIp,
            string.IsNullOrEmpty(userAgent) ? "(empty)" : userAgent);

        if (string.IsNullOrWhiteSpace(providerName))
            return ToActionResult(Result<WebhookInboxDto>.Failure(ErrorCodes.VALIDATION_ERROR, "providerName route value is required."));

        var headers = Request.Headers
            .Where(h => h.Key.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());

        var signature = SignatureHeaderCandidates
            .Select(name => headers.TryGetValue(name, out var v) ? v : null)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        headers.TryGetValue(IdempotencyHeader, out var idempotencyKey);
        headers.TryGetValue(ProviderEventIdHeader, out var providerEventId);

        // PayFast bypasses the generic JSON inbox pipeline. ITNs are
        // form-urlencoded with MD5+passphrase signing; the bridge
        // parses the form body, persists an inbox audit row, and
        // dispatches to PayFastNotifyHandler which materialises the
        // OrderIntent on COMPLETE.
        if (string.Equals(providerName.Trim(), "payfast", StringComparison.OrdinalIgnoreCase))
        {
            return await HandlePayFastWebhookAsync(remoteIp, userAgent, signature, providerEventId, idempotencyKey, cancellationToken);
        }

        // Generic inbox path (non-PayFast). Reads body as a raw stream.
        string rawPayload;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
        {
            rawPayload = await reader.ReadToEndAsync(cancellationToken);
        }

        var dto = new PaymentWebhookRequestDto
        {
            Provider = MapProvider(providerName),
            ProviderName = providerName.Trim(),
            ProviderEventId = string.IsNullOrWhiteSpace(providerEventId) ? null : providerEventId,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
            RawPayload = rawPayload,
            SignatureHeader = signature,
            Headers = headers
        };

        var result = await _service.ReceivePaymentWebhookAsync(dto, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("admin/inbox")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] WebhookInboxFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/inbox/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    private static WebhookProvider MapProvider(string providerName) =>
        providerName.Trim().ToLowerInvariant() switch
        {
            "payfast" => WebhookProvider.PayFast,
            "peachpayments" or "peach" => WebhookProvider.PeachPayments,
            "paystack" => WebhookProvider.Paystack,
            "yoco" => WebhookProvider.Yoco,
            "ozow" => WebhookProvider.Ozow,
            "manual" => WebhookProvider.Manual,
            _ => WebhookProvider.Other
        };

    /// <summary>
    /// PayFast webhook branch. Reads the body via Request.ReadFormAsync
    /// for form-urlencoded ITNs (the 2026-06 fix) and, when the
    /// temporary UAT forensic capture is enabled, snapshots the entire
    /// request to a JSON file for offline diagnosis.
    /// </summary>
    private async Task<IActionResult> HandlePayFastWebhookAsync(
        string remoteIp,
        string userAgent,
        string? signature,
        string? providerEventId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // TEMPORARY UAT FORENSIC CAPTURE — start. Allocates only when
        // the env-var toggle is on so production behaviour is unaffected.
        // Remove or hard-gate to non-Production before going live.
        PayFastForensicSnapshot? diag = null;
        if (_forensicCapture.IsEnabled)
        {
            diag = new PayFastForensicSnapshot
            {
                Method = Request.Method,
                Path = Request.Path.Value ?? string.Empty,
                QueryString = Request.QueryString.HasValue ? Request.QueryString.Value : null,
                Scheme = Request.Scheme,
                Host = Request.Host.Value,
                RemoteIp = remoteIp,
                UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
                ContentType = Request.ContentType,
                ContentLength = Request.ContentLength,
                HasFormContentType = Request.HasFormContentType,
                RedactedHeaders = BuildRedactedHeaders(Request.Headers),
            };
            _logger.LogInformation(
                "[payment][payfast][forensic_start] diagId={DiagId} method={Method} path={Path} contentType={ContentType} contentLength={ContentLength} hasFormContentType={HasForm}",
                diag.DiagId, diag.Method, diag.Path, diag.ContentType, diag.ContentLength, diag.HasFormContentType);

            // Enable buffering so we can read raw body AND let
            // ReadFormAsync re-read the same stream. Required because
            // Request.Body is a forward-only non-seekable stream by
            // default — see the 2026-06 empty-body incident.
            Request.EnableBuffering();

            using (var rawReader = new StreamReader(Request.Body, leaveOpen: true))
            {
                diag.RawBody = await rawReader.ReadToEndAsync(cancellationToken);
                diag.RawBodyLength = diag.RawBody.Length;
            }
            Request.Body.Position = 0;

            _logger.LogInformation(
                "[payment][payfast][forensic_body] diagId={DiagId} rawLength={RawLength} hasFormContentType={HasForm} contentType={ContentType}",
                diag.DiagId, diag.RawBodyLength, diag.HasFormContentType, diag.ContentType);
        }

        // CRITICAL: read the body via ReadFormAsync, NOT as a raw
        // StreamReader on Request.Body. Reading Request.Body as a
        // stream after any other component touched it gave an
        // EMPTY payload in UAT (SHA-256 of "" = E3B0C44…), which
        // produced a synthetic event id of
        // `payfast:(no-ref):(no-pf):(no-status)` and made every
        // subsequent malformed ITN appear as a duplicate of the
        // first one. ReadFormAsync handles form-urlencoded
        // payloads correctly regardless of buffering state.
        Dictionary<string, string>? preParsedFields = null;
        List<KeyValuePair<string, string>>? postedFieldsOrdered = null;
        string canonicalRawBody;
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            // Capture both shapes: dictionary for lookups, list for
            // signature (order matters per PayFast ITN spec).
            preParsedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            postedFieldsOrdered = new List<KeyValuePair<string, string>>(form.Count);
            foreach (var kv in form)
            {
                var value = kv.Value.ToString();
                preParsedFields[kv.Key] = value;
                postedFieldsOrdered.Add(new KeyValuePair<string, string>(kv.Key, value));
            }
            canonicalRawBody = string.Empty;
            _logger.LogInformation(
                "[payment][webhook][form_read] hasFormContentType=True formKeyCount={FormKeyCount} formKeys={FormKeys}",
                preParsedFields.Count,
                preParsedFields.Count > 0 ? string.Join(",", preParsedFields.Keys) : "(none)");

            if (diag is not null)
            {
                diag.FieldsSource = "preParsed";
                diag.FormKeyCount = preParsedFields.Count;
                diag.FormKeys = postedFieldsOrdered.Select(kv => kv.Key).ToList();
                diag.FormValues = new Dictionary<string, string>(preParsedFields, StringComparer.OrdinalIgnoreCase);
                diag.ParsedSummary = ExtractParsedSummary(preParsedFields);
                _logger.LogInformation(
                    "[payment][payfast][forensic_fields] diagId={DiagId} fieldsSource=preParsed keys={Keys}",
                    diag.DiagId, string.Join(",", diag.FormKeys));
            }
        }
        else
        {
            // Non-form content type fallback. If diag is on we've
            // already buffered + reset; if diag is off we buffer now
            // so the body can be re-read if needed.
            if (diag is null) Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            canonicalRawBody = await reader.ReadToEndAsync(cancellationToken);
            if (diag is null) Request.Body.Position = 0;
            _logger.LogInformation(
                "[payment][webhook][form_read] hasFormContentType=False rawBodyLength={RawBodyLength} contentType={ContentType}",
                canonicalRawBody.Length, Request.ContentType);

            if (diag is not null)
            {
                diag.FieldsSource = "rawBody";
                var fallbackParse = ParseFormBodyForDiag(canonicalRawBody);
                diag.FormKeyCount = fallbackParse.Count;
                diag.FormKeys = fallbackParse.Keys.ToList();
                diag.FormValues = fallbackParse;
                diag.ParsedSummary = ExtractParsedSummary(fallbackParse);
                _logger.LogInformation(
                    "[payment][payfast][forensic_fields] diagId={DiagId} fieldsSource=rawBody keys={Keys}",
                    diag.DiagId, string.Join(",", diag.FormKeys));
            }
        }

        PayFastWebhookBridgeOutcome bridgeOutcome;
        try
        {
            bridgeOutcome = await _payFastBridge.HandleAsync(
                canonicalRawBody,
                preParsedFields,
                postedFieldsOrdered,
                signatureHeader: signature,
                providerEventIdHeader: string.IsNullOrWhiteSpace(providerEventId) ? null : providerEventId,
                idempotencyKeyHeader: string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                cancellationToken);
        }
        catch (Exception ex)
        {
            if (diag is not null)
            {
                diag.ExceptionMessage = ex.Message;
                diag.ExceptionStackTrace = ex.ToString();
                var crashPath = await _forensicCapture.TryWriteAsync(diag, cancellationToken);
                _logger.LogError(ex,
                    "[payment][payfast][forensic_exception] diagId={DiagId} path={Path} message={Message}",
                    diag.DiagId, crashPath, ex.Message);
            }
            throw;
        }

        if (diag is not null)
        {
            diag.GeneratedProviderEventId = bridgeOutcome.ProviderEventId;
            diag.Result = new BridgeResultSnapshot
            {
                Accepted = bridgeOutcome.Accepted,
                Message = bridgeOutcome.Message,
                InboxId = bridgeOutcome.InboxId,
                ProviderEventId = bridgeOutcome.ProviderEventId,
                Signature = BuildSignatureForensicBlock(bridgeOutcome),
            };
            _logger.LogInformation(
                "[payment][payfast][forensic_result] diagId={DiagId} accepted={Accepted} message={Message} inboxId={InboxId} providerEventId={ProviderEventId} signaturePosted={SignaturePosted} signatureComputed={SignatureComputed} signatureMatch={SignatureMatch} signatureAlgorithm={SignatureAlgorithm}",
                diag.DiagId, diag.Result.Accepted, diag.Result.Message, diag.Result.InboxId, diag.Result.ProviderEventId,
                diag.Result.Signature?.PostedSignature ?? "(n/a)",
                diag.Result.Signature?.ComputedSignature ?? "(n/a)",
                diag.Result.Signature?.Match,
                diag.Result.Signature?.Algorithm ?? "(n/a)");

            var path = await _forensicCapture.TryWriteAsync(diag, cancellationToken);
            if (!string.IsNullOrEmpty(path))
            {
                _logger.LogInformation(
                    "[payment][payfast][forensic_file] diagId={DiagId} path={Path}",
                    diag.DiagId, path);
            }
        }

        return Ok(new
        {
            accepted = bridgeOutcome.Accepted,
            message = bridgeOutcome.Message,
            inboxId = bridgeOutcome.InboxId,
        });
    }

    private static Dictionary<string, string> BuildRedactedHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            result[header.Key] = ShouldRedactHeader(header.Key) ? "[REDACTED]" : header.Value.ToString();
        }
        return result;
    }

    private static bool ShouldRedactHeader(string headerName)
    {
        if (AlwaysRedactHeaders.Contains(headerName)) return true;
        foreach (var pattern in SubstringRedactPatterns)
        {
            if (headerName.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static PayFastParsedSummary ExtractParsedSummary(IDictionary<string, string> fields)
    {
        return new PayFastParsedSummary
        {
            MPaymentId = TryGet(fields, "m_payment_id"),
            PfPaymentId = TryGet(fields, "pf_payment_id"),
            PaymentStatus = TryGet(fields, "payment_status"),
            AmountGross = TryGetDecimal(fields, "amount_gross"),
            AmountFee = TryGetDecimal(fields, "amount_fee"),
            AmountNet = TryGetDecimal(fields, "amount_net"),
            MerchantId = TryGet(fields, "merchant_id"),
            SignaturePresent = !string.IsNullOrEmpty(TryGet(fields, "signature")),
        };
    }

    private static string? TryGet(IDictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private static decimal? TryGetDecimal(IDictionary<string, string> fields, string key)
    {
        var s = TryGet(fields, key);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return decimal.TryParse(s, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static PayFastSignatureForensicBlock? BuildSignatureForensicBlock(PayFastWebhookBridgeOutcome outcome)
    {
        // Always emit posted + computed even when SignatureDebug is
        // null (e.g. duplicate short-circuit). Only when we have the
        // full debug object do we surface algorithm + field order +
        // redacted base string.
        if (outcome.SignatureDebug is null
            && string.IsNullOrEmpty(outcome.PostedSignature)
            && string.IsNullOrEmpty(outcome.ComputedSignature))
        {
            return null;
        }
        return new PayFastSignatureForensicBlock
        {
            PostedSignature = outcome.PostedSignature,
            ComputedSignature = outcome.ComputedSignature,
            Match = !string.IsNullOrEmpty(outcome.PostedSignature)
                 && string.Equals(outcome.PostedSignature, outcome.ComputedSignature, StringComparison.OrdinalIgnoreCase),
            PassphraseConfigured = outcome.SignatureDebug?.PassphraseConfigured ?? false,
            FieldNamesInSignatureOrder = outcome.SignatureDebug?.FieldNamesInOrder ?? new List<string>(),
            BaseStringRedacted = outcome.SignatureDebug?.BaseStringRedacted,
            Algorithm = outcome.SignatureDebug?.Algorithm,
        };
    }

    private static Dictionary<string, string> ParseFormBodyForDiag(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return result;
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                result[System.Web.HttpUtility.UrlDecode(pair)] = string.Empty;
                continue;
            }
            var key = System.Web.HttpUtility.UrlDecode(pair[..eq]);
            var value = System.Web.HttpUtility.UrlDecode(pair[(eq + 1)..]);
            if (!string.IsNullOrEmpty(key)) result[key] = value;
        }
        return result;
    }
}

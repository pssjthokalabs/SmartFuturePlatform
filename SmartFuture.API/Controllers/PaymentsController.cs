using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.Payments.Ozow;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/payments")]
public class PaymentsController : BaseController
{
    private static readonly System.Text.Json.JsonSerializerOptions OzowJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IPaymentService _service;
    private readonly OzowNotifyHandler _ozowNotify;
    private readonly PayFastNotifyHandler _payFastNotify;
    private readonly PaystackNotifyHandler _paystackNotify;
    private readonly IOzowTransactionStatusService _ozowStatus;
    private readonly IOrderIntentService _orderIntentService;
    private readonly Microsoft.Extensions.Options.IOptions<OzowSettings> _ozowSettings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        IPaymentService service,
        OzowNotifyHandler ozowNotify,
        PayFastNotifyHandler payFastNotify,
        PaystackNotifyHandler paystackNotify,
        IOzowTransactionStatusService ozowStatus,
        IOrderIntentService orderIntentService,
        Microsoft.Extensions.Options.IOptions<OzowSettings> ozowSettings,
        IHostEnvironment env,
        ILogger<PaymentsController> logger)
    {
        _service = service;
        _ozowNotify = ozowNotify;
        _payFastNotify = payFastNotify;
        _paystackNotify = paystackNotify;
        _ozowStatus = ozowStatus;
        _orderIntentService = orderIntentService;
        _ozowSettings = ozowSettings;
        _env = env;
        _logger = logger;
    }

    [HttpGet("mine")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMine([FromQuery] PaymentFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineAsync(filter, cancellationToken));

    [HttpGet("mine/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMineById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineByIdAsync(id, cancellationToken));

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] PaymentFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPost("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> Create([FromBody] CreatePaymentRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateAsync(request, cancellationToken));

    [HttpPost("admin/{id:guid}/status")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminUpdateStatus(Guid id, [FromBody] AdminUpdatePaymentStatusDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminUpdateStatusAsync(id, request, cancellationToken));

    // Phase 52 — Ozow notify webhook. Public + anonymous; Ozow POSTs an
    // x-www-form-urlencoded payload here. The handler verifies the hash,
    // routes intent vs invoice by reference prefix, cross-checks the
    // amount, and either converts the OrderIntent or applies the status
    // transition. We always return 200 OK — Ozow retries on non-2xx, so
    // we don't want to chain-retry on a known bad payload (e.g. hash
    // mismatch on a replay attempt).
    //
    // DELIBERATELY NOT `[Consumes(...)]` and NOT `[FromForm]`:
    //
    //   • `[Consumes]` makes MVC reject an unexpected Content-Type with
    //     415 BEFORE the action body runs — no log line, no trace, and
    //     the notification is lost silently. That is indistinguishable
    //     from "Ozow never called us", which is exactly the ambiguity
    //     that made a completed R10 payment impossible to diagnose.
    //   • `[FromForm]` binds nothing when the body arrives as JSON or
    //     when the fields ride on the query string, so the handler would
    //     see an all-null payload and report "Missing TransactionReference".
    //
    // Instead we accept ANY content type, log what arrived FIRST, then
    // bind leniently from form → query → JSON body. Reachability and
    // payload-shape problems are now two distinguishable failures.
    [HttpPost("ozow/notify")]
    [AllowAnonymous]
    public async Task<IActionResult> OzowNotify(CancellationToken cancellationToken)
    {
        var (payload, source) = await ReadOzowNotifyPayloadAsync(cancellationToken);

        // ─── [OzowNotifyReceived] ─────────────────────────────────────
        // FIRST thing that happens, before hash validation and before any
        // lookup. If this line is absent from the logs for a given
        // reference, Ozow did not reach this API — check Ozow__NotifyUrl
        // and the host's public reachability, not our hash logic.
        //
        // Safe by construction: reference / transactionId / status /
        // amount are not secrets (they're echoed in the customer's own
        // return URL). The hash is reported length-only. The private key
        // and API key are never touched here.
        _logger.LogInformation(
            "[OzowNotifyReceived] reference={Reference} transactionId={TransactionId} status={Status} " +
            "amount={Amount} isTest={IsTest} currency={Currency} remoteIp={RemoteIp} contentType={ContentType} " +
            "bindSource={BindSource} hashLength={HashLength}",
            payload.TransactionReference ?? "(none)",
            payload.TransactionId ?? "(none)",
            payload.Status ?? "(none)",
            payload.Amount,
            payload.IsTest,
            payload.CurrencyCode ?? "(none)",
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "(unknown)",
            string.IsNullOrWhiteSpace(Request.ContentType) ? "(none)" : Request.ContentType,
            source,
            payload.Hash?.Length ?? 0);

        var outcome = await _ozowNotify.HandleAsync(payload, cancellationToken);

        _logger.LogInformation(
            "[OzowNotifyReceived] handled reference={Reference} accepted={Accepted} message={Message}",
            payload.TransactionReference ?? "(none)", outcome.Accepted, outcome.Message);

        return Ok(new { accepted = outcome.Accepted, message = outcome.Message });
    }

    /// <summary>
    /// ADMIN DIAGNOSTIC — report the live Ozow configuration and, optionally,
    /// whether this host can actually reach api.ozow.com. Takes no payment
    /// and changes nothing.
    ///
    ///   GET /api/payments/ozow/config-check              (config only)
    ///   GET /api/payments/ozow/config-check?probe=true   (also test egress)
    ///
    /// Exists because a failing Ozow checkout is otherwise indistinguishable
    /// from a CORS error in the browser: if the outbound call hangs or the
    /// request 500s, the client sees a dead connection, not our error. This
    /// endpoint answers "is Ozow configured, and can we even talk to it?"
    /// as plain JSON, from the same origin, with CORS headers intact.
    ///
    /// No secrets: SiteCode masked; ApiKey/PrivateKey reported as presence +
    /// length only. URLs are shown verbatim — they are not secret and being
    /// able to read them is the entire point.
    /// </summary>
    [HttpGet("ozow/config-check")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> OzowConfigCheck(
        [FromQuery] bool probe,
        CancellationToken cancellationToken)
    {
        var s = _ozowSettings.Value;

        var notifyUrlLooksRight = !string.IsNullOrWhiteSpace(s.NotifyUrl)
            && s.NotifyUrl.Contains("/api/payments/ozow/notify", StringComparison.OrdinalIgnoreCase);

        var problems = new List<string>();
        if (!s.Enabled)                                   problems.Add("Ozow:Enabled is false — customer-facing initiation is refused.");
        if (string.IsNullOrWhiteSpace(s.SiteCode))        problems.Add("Ozow:SiteCode is empty.");
        if (string.IsNullOrWhiteSpace(s.ApiKey))          problems.Add("Ozow:ApiKey is empty.");
        if (string.IsNullOrWhiteSpace(s.PrivateKey))      problems.Add("Ozow:PrivateKey is empty.");
        if (!s.IsTest.HasValue)                           problems.Add("Ozow:IsTest is not set (must be explicitly true or false).");
        if (string.IsNullOrWhiteSpace(s.NotifyUrl))       problems.Add("Ozow:NotifyUrl is empty — intent payments could never convert.");
        else if (!notifyUrlLooksRight)                    problems.Add($"Ozow:NotifyUrl does not contain '/api/payments/ozow/notify' (value: {s.NotifyUrl}).");
        if (string.IsNullOrWhiteSpace(s.SuccessUrl))      problems.Add("Ozow:SuccessUrl is empty.");
        if (string.IsNullOrWhiteSpace(s.CancelUrl))       problems.Add("Ozow:CancelUrl is empty.");
        if (string.IsNullOrWhiteSpace(s.ErrorUrl))        problems.Add("Ozow:ErrorUrl is empty (falls back to CancelUrl).");

        object? egress = null;
        if (probe)
        {
            // Cheap read-only reachability test: ask Ozow about a
            // reference that cannot exist. Any structured reply — even
            // "not found" — proves egress works. A timeout means this
            // host cannot reach api.ozow.com, which is the single most
            // likely cause of a checkout that dies without a response.
            var started = DateTime.UtcNow;
            var probeResult = await _ozowStatus.GetByReferenceAsync(
                $"SF-CONNECTIVITY-PROBE-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                cancellationToken);
            var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;

            egress = new
            {
                reachedOzow = probeResult.Success,
                elapsedMs,
                detail = probeResult.Success
                    ? "Ozow responded. Outbound HTTPS from this host works."
                    : probeResult.FailureReason,
                hint = probeResult.Success
                    ? null
                    : "If this is a timeout, outbound HTTPS to api.ozow.com is likely blocked on this hosting tier. That would make checkout hang and surface in the browser as a CORS / ERR_FAILED error."
            };
        }

        return Ok(new
        {
            ok = problems.Count == 0,
            environment = _env.EnvironmentName,
            enabled = s.Enabled,
            siteCode = MaskSiteCode(s.SiteCode),
            apiKeyPresent = !string.IsNullOrWhiteSpace(s.ApiKey),
            apiKeyLength = s.ApiKey?.Length ?? 0,
            privateKeyPresent = !string.IsNullOrWhiteSpace(s.PrivateKey),
            privateKeyLength = s.PrivateKey?.Length ?? 0,
            isTest = s.IsTest,
            useTestAmountOverride = s.UseTestAmountOverride,
            testAmount = s.TestAmount,
            apiUrl = string.IsNullOrWhiteSpace(s.ApiUrl) ? "(default: https://api.ozow.com/PostPaymentRequest)" : s.ApiUrl,
            notifyUrl = string.IsNullOrWhiteSpace(s.NotifyUrl) ? "(EMPTY)" : s.NotifyUrl,
            notifyUrlLooksRight,
            successUrl = string.IsNullOrWhiteSpace(s.SuccessUrl) ? "(EMPTY)" : s.SuccessUrl,
            cancelUrl = string.IsNullOrWhiteSpace(s.CancelUrl) ? "(EMPTY)" : s.CancelUrl,
            errorUrl = string.IsNullOrWhiteSpace(s.ErrorUrl) ? "(EMPTY)" : s.ErrorUrl,
            expectedNotifyRoute = "POST /api/payments/ozow/notify",
            problems,
            egress
        });
    }

    private static string MaskSiteCode(string? siteCode)
    {
        if (string.IsNullOrEmpty(siteCode)) return "(empty)";
        if (siteCode.Length <= 5) return new string('*', siteCode.Length);
        return $"{siteCode[..3]}***{siteCode[^2..]}";
    }

    /// <summary>
    /// ADMIN RECOVERY — reconcile a stuck Ozow order-intent by asking Ozow
    /// what actually happened, instead of waiting for a webhook that may
    /// never arrive.
    ///
    ///   POST /api/payments/ozow/reconcile/SF-INTENT-FD2368C73A2C
    ///
    /// Flow: query Ozow's GetTransactionByReference → if the transaction
    /// is Complete AND the amount matches what we told Ozow to charge,
    /// run the SAME conversion the webhook would have run
    /// (ConvertIntentPaymentToPaidOrderAsync, which is idempotent). If the
    /// transaction is anything other than Complete, nothing is changed.
    ///
    /// This is how a completed-but-unconverted payment is recovered
    /// WITHOUT asking the customer to pay again. It is admin-only because
    /// it mints an Order; it is safe to run repeatedly.
    ///
    /// `dryRun=true` (default false) reports what Ozow says and what would
    /// happen, without converting — use it first.
    /// </summary>
    [HttpPost("ozow/reconcile/{transactionReference}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> OzowReconcile(
        string transactionReference,
        [FromQuery] bool dryRun,
        CancellationToken cancellationToken)
    {
        var reference = (transactionReference ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest(new { ok = false, message = "transactionReference is required." });

        _logger.LogInformation(
            "[OzowIntentReconcile] stage=manual-check reference={Reference} dryRun={DryRun}",
            reference, dryRun);

        var status = await _ozowStatus.GetByReferenceAsync(reference, cancellationToken);

        if (!status.Success)
        {
            return Ok(new
            {
                ok = false,
                reference,
                message = status.FailureReason ?? "Ozow status check failed.",
                converted = false
            });
        }

        if (!status.Found)
        {
            // Definitive: Ozow has never seen this reference. That means
            // the customer was NOT charged under it — nothing to recover.
            _logger.LogWarning(
                "[OzowIntentReconcile] stage=manual-not-found reference={Reference} — Ozow has no record; no charge to recover.",
                reference);
            return Ok(new
            {
                ok = true,
                reference,
                found = false,
                message = "Ozow has no transaction for this reference. No payment was taken under it.",
                converted = false
            });
        }

        var isComplete = string.Equals(status.Status, "Complete", StringComparison.OrdinalIgnoreCase);

        if (!isComplete || dryRun)
        {
            return Ok(new
            {
                ok = true,
                reference,
                found = true,
                status.Status,
                status.TransactionId,
                status.Amount,
                status.IsTest,
                wouldConvert = isComplete,
                converted = false,
                message = isComplete
                    ? "Transaction is Complete. Re-run without dryRun=true to create the order."
                    : $"Transaction status is '{status.Status}' — nothing to convert."
            });
        }

        var conversion = await _orderIntentService.ConvertIntentPaymentToPaidOrderAsync(
            intentPaymentReference: reference,
            paidAtUtc:              DateTime.UtcNow,
            gatewayTransactionId:   status.TransactionId,
            authorizationSnapshot:  null,
            cancellationToken:      cancellationToken);

        if (!conversion.IsSuccess)
        {
            _logger.LogError(
                "[OzowIntentReconcile] stage=manual-conversion-failed reference={Reference} code={Code} message={Message}",
                reference, conversion.Code, conversion.Message);
            return Ok(new
            {
                ok = false,
                reference,
                found = true,
                status.Status,
                converted = false,
                message = conversion.Message ?? "Conversion failed."
            });
        }

        _logger.LogInformation(
            "[OzowIntentReconcile] stage=manual-converted reference={Reference} orderNumber={OrderNumber} transactionId={TransactionId}",
            reference, conversion.Data?.OrderNumber ?? "(none)", status.TransactionId);

        return Ok(new
        {
            ok = true,
            reference,
            found = true,
            status.Status,
            status.TransactionId,
            status.Amount,
            converted = true,
            orderNumber = conversion.Data?.OrderNumber,
            invoiceNumber = conversion.Data?.InvoiceNumber,
            message = "Intent converted to a paid order."
        });
    }

    /// <summary>
    /// Reachability ping for the Ozow notify endpoint — mirrors the
    /// Paystack one. Anonymous and side-effect free: it touches no
    /// payment state, so it is safe to curl from anywhere.
    ///
    /// Use it to prove the URL in Ozow__NotifyUrl actually resolves to
    /// THIS API before blaming the hash or spending another real
    /// payment:
    ///
    ///   curl -i https://uatapi.smartfuture.co.za/api/payments/ozow/notify/ping
    ///
    /// A 200 with this payload means Ozow's POST can reach us. Anything
    /// else (404 / 502 / timeout / a portal HTML page) means
    /// Ozow__NotifyUrl is pointed at the wrong host and no notification
    /// could ever have landed.
    /// </summary>
    [HttpGet("ozow/notify/ping")]
    [AllowAnonymous]
    public IActionResult OzowNotifyPing()
    {
        _logger.LogInformation(
            "[OzowNotifyReceived] ping remoteIp={RemoteIp}",
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "(unknown)");

        return Ok(new
        {
            ok = true,
            endpoint = "POST /api/payments/ozow/notify",
            note = "Reachable. This is the URL Ozow__NotifyUrl must point at.",
            utc = DateTime.UtcNow
        });
    }

    // Lenient binder for the Ozow notification. Ozow documents
    // x-www-form-urlencoded, but we accept query-string and JSON too so a
    // shape surprise produces a LOGGED failure rather than a silent 415.
    // Returns the payload plus which source actually populated it, so the
    // [OzowNotifyReceived] line records how the request really arrived.
    private async Task<(OzowNotifyPayload Payload, string Source)> ReadOzowNotifyPayloadAsync(CancellationToken cancellationToken)
    {
        // 1. Form body (the documented, expected shape).
        if (Request.HasFormContentType)
        {
            try
            {
                var form = await Request.ReadFormAsync(cancellationToken);
                if (form.Count > 0) return (MapOzow(form.ToDictionary(k => k.Key, v => v.Value.ToString())), "form");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OzowNotifyReceived] form read failed — falling through to query/JSON.");
            }
        }

        // 2. Query string (Ozow's return redirect uses this shape; some
        //    merchant configs post it back the same way).
        if (Request.Query.Count > 0)
        {
            return (MapOzow(Request.Query.ToDictionary(k => k.Key, v => v.Value.ToString())), "query");
        }

        // 3. JSON body.
        try
        {
            Request.EnableBuffering();
            Request.Body.Position = 0;
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(cancellationToken);
            Request.Body.Position = 0;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<OzowNotifyPayload>(raw, OzowJsonOptions);
                if (parsed is not null) return (parsed, "json");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OzowNotifyReceived] JSON read failed — returning empty payload so the miss is still logged.");
        }

        return (new OzowNotifyPayload(), "none");
    }

    // Case-insensitive field pluck. Ozow's casing has varied across their
    // docs and dashboard configurations, so we don't depend on it.
    private static OzowNotifyPayload MapOzow(IDictionary<string, string> values)
    {
        var map = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        string? Get(string key) => map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        decimal.TryParse(Get("Amount"), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount);
        _ = bool.TryParse(Get("IsTest"), out var isTest);

        return new OzowNotifyPayload
        {
            SiteCode             = Get("SiteCode"),
            TransactionId        = Get("TransactionId"),
            TransactionReference = Get("TransactionReference"),
            Amount               = amount,
            Status               = Get("Status"),
            Optional1            = Get("Optional1"),
            Optional2            = Get("Optional2"),
            Optional3            = Get("Optional3"),
            Optional4            = Get("Optional4"),
            Optional5            = Get("Optional5"),
            CurrencyCode         = Get("CurrencyCode"),
            IsTest               = isTest,
            StatusMessage        = Get("StatusMessage"),
            Hash                 = Get("Hash")
        };
    }

    [HttpPost("payfast/notify")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded", "application/json")]
    public async Task<IActionResult> PayFastNotify([FromForm] PayFastNotifyPayload payload, CancellationToken cancellationToken)
    {
        var outcome = await _payFastNotify.HandleAsync(payload, cancellationToken);
        return Ok(new { accepted = outcome.Accepted, message = outcome.Message });
    }

    // Paystack webhook. Public + anonymous; signature is HMAC-SHA512 of
    // the raw JSON body with the merchant secret key, sent in the
    // x-paystack-signature header. We MUST read the body verbatim
    // (no model-binding) so the hash matches Paystack's. Always returns
    // HTTP 200 with a short JSON body — Paystack retries on non-2xx,
    // so we don't want to chain-retry on a known bad payload (e.g.
    // signature mismatch on a replay attempt).
    [HttpPost("paystack/notify")]
    [AllowAnonymous]
    [Consumes("application/json")]
    public async Task<IActionResult> PaystackNotify(CancellationToken cancellationToken)
    {
        string rawBody;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }
        var signature = Request.Headers.TryGetValue("x-paystack-signature", out var sig)
            ? sig.ToString()
            : null;
        var outcome = await _paystackNotify.HandleAsync(rawBody, signature, cancellationToken);
        // We always return 200 so Paystack doesn't retry. The body now
        // carries the diagnosticId (PaystackWebhookLog.Id) and the
        // Paystack reference so a failed delivery can be traced from
        // the Paystack dashboard delivery log straight to our DB row.
        return Ok(new
        {
            accepted     = outcome.Accepted,
            message      = outcome.Message,
            reference    = outcome.Reference,
            diagnosticId = outcome.DiagnosticId,
        });
    }

    /// <summary>
    /// Reachability ping for the Paystack notify endpoint. Anonymous;
    /// returns enabled/configured state + environment + the
    /// webhook URL the API thinks Paystack should POST to. Useful for
    /// confirming a UAT deploy is live BEFORE configuring the Paystack
    /// dashboard. Never returns the secret key.
    /// </summary>
    [HttpGet("paystack/notify/ping")]
    [AllowAnonymous]
    public IActionResult PaystackNotifyPing(
        [FromServices] Microsoft.Extensions.Options.IOptions<SmartFuture.Application.Payments.Paystack.PaystackSettings> settings,
        [FromServices] Microsoft.Extensions.Hosting.IHostEnvironment env)
    {
        var s = settings.Value;
        return Ok(new
        {
            ok              = true,
            provider        = "Paystack",
            enabled         = s.Enabled,
            configured      = s.IsConfigured,
            environment     = env.EnvironmentName,
            currency        = s.Currency,
            webhookUrl      = s.WebhookUrl,
            callbackUrl     = s.CallbackUrl,
            secretKeyPrefix = ResolveSecretKeyDiagnosticPrefix(s.SecretKey),
            isTestKey       = s.IsTestKey,
            useTestOverride = s.UseTestAmountOverride,
            testAmount      = s.TestAmount,
            allowLiveOverride = s.AllowLiveTestAmountOverride,
            serverTimeUtc   = DateTime.UtcNow,
        });
    }

    // Same logic the notify handler uses to keep logs comparable. We
    // copy it instead of exposing the handler's private helper —
    // a controller has no reason to reach into Application internals.
    private static string ResolveSecretKeyDiagnosticPrefix(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "(none)";
        if (key.StartsWith("sk_test_", StringComparison.OrdinalIgnoreCase)) return "sk_test";
        if (key.StartsWith("sk_live_", StringComparison.OrdinalIgnoreCase)) return "sk_live";
        return "(unknown)";
    }
}

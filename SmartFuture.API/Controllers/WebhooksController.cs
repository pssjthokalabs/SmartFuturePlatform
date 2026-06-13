using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartFuture.API.Extensions;
using SmartFuture.Application.Payments.PayFast;
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
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(IWebhookInboxService service, IPayFastWebhookBridge payFastBridge, ILogger<WebhooksController> logger)
    {
        _service = service;
        _payFastBridge = payFastBridge;
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
            string canonicalRawBody;
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(cancellationToken);
                preParsedFields = form.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
                // Canonical raw body for hashing — see the bridge's
                // BuildCanonicalBody helper. We pass an empty string
                // here because the bridge prefers preParsedFields.
                canonicalRawBody = string.Empty;
                _logger.LogInformation(
                    "[payment][webhook][form_read] hasFormContentType=True formKeyCount={FormKeyCount} formKeys={FormKeys}",
                    preParsedFields.Count,
                    preParsedFields.Count > 0 ? string.Join(",", preParsedFields.Keys) : "(none)");
            }
            else
            {
                // Non-form content type fallback. PayFast retries
                // occasionally arrive without the Content-Type header
                // populated by some proxies. Enable buffering so the
                // body can be re-read if needed downstream.
                Request.EnableBuffering();
                using var reader = new StreamReader(Request.Body, leaveOpen: true);
                canonicalRawBody = await reader.ReadToEndAsync(cancellationToken);
                Request.Body.Position = 0;
                _logger.LogInformation(
                    "[payment][webhook][form_read] hasFormContentType=False rawBodyLength={RawBodyLength} contentType={ContentType}",
                    canonicalRawBody.Length, Request.ContentType);
            }

            var bridgeOutcome = await _payFastBridge.HandleAsync(
                canonicalRawBody,
                preParsedFields,
                signatureHeader: signature,
                providerEventIdHeader: string.IsNullOrWhiteSpace(providerEventId) ? null : providerEventId,
                idempotencyKeyHeader: string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                cancellationToken);

            return Ok(new
            {
                accepted = bridgeOutcome.Accepted,
                message = bridgeOutcome.Message,
                inboxId = bridgeOutcome.InboxId,
            });
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
}

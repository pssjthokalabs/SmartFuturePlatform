using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartFuture.API.Extensions;
using SmartFuture.Application.Openserve;

namespace SmartFuture.API.Controllers;

// Openserve Fulfilment API callback + event-notification receivers
// (brief Priority 2). Deliberately a sibling to WebhooksController, not
// a branch inside it — Openserve payloads are a different wire format
// entirely (TMF622-flavoured, not a payment-gateway shape), and both
// endpoints here delegate all parsing/idempotency/correlation logic to
// IOpenserveInboundProcessor -> IOpenserveOrderUpdatePipeline, mirroring
// (not reusing) the "log first, never 500, tolerate duplicates/
// malformed/out-of-order/unknown-correlation" shape WebhookInboxService
// already established for payments.
//
// Auth: see IOpenserveCallbackAuthValidator remarks — the spec
// documents no callback auth scheme. Both endpoints stay publicly
// reachable (Openserve needs a URL to register during onboarding
// regardless of OpenserveFulfilment:Enabled) but a request that fails
// the configured validator (default: none configured, so nothing to
// fail) is rejected before any parsing happens.
[Route("api/openserve")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingExtensions.WebhookPolicy)]
[RequestSizeLimit(512 * 1024)]
public class OpenserveController : BaseController
{
    private readonly IOpenserveInboundProcessor _processor;
    private readonly IOpenserveCallbackAuthValidator _authValidator;
    private readonly ILogger<OpenserveController> _logger;

    public OpenserveController(
        IOpenserveInboundProcessor processor, IOpenserveCallbackAuthValidator authValidator, ILogger<OpenserveController> logger)
    {
        _processor = processor;
        _authValidator = authValidator;
        _logger = logger;
    }

    /// <summary>Target for the mandatory "ReplyToAddress" header sent on every outbound order call (spec §4.1.1, §1.7).</summary>
    [HttpPost("callback")]
    public Task<IActionResult> Callback(CancellationToken cancellationToken) => HandleAsync("callback", cancellationToken);

    /// <summary>Target for Openserve's separately-registered event-notification endpoint (spec §1.7, §8).</summary>
    [HttpPost("events")]
    public Task<IActionResult> Events(CancellationToken cancellationToken) => HandleAsync("events", cancellationToken);

    private async Task<IActionResult> HandleAsync(string kind, CancellationToken cancellationToken)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        // First-line hit log, before any body read — proves the
        // request reached us at all (see WebhooksController for the
        // same reasoning applied to payment webhooks).
        _logger.LogInformation("[openserve][{Kind}][hit] remoteIp={RemoteIp} contentLength={ContentLength}", kind, remoteIp, Request.ContentLength);

        var headers = Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());

        if (!_authValidator.IsValid(headers, remoteIp))
        {
            _logger.LogWarning("[openserve][{Kind}][auth_rejected] remoteIp={RemoteIp}", kind, remoteIp);
            return Unauthorized(new { accepted = false, message = "Not authorized." });
        }

        string rawPayload;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
        {
            rawPayload = await reader.ReadToEndAsync(cancellationToken);
        }

        try
        {
            var result = kind == "callback"
                ? await _processor.ProcessCallbackAsync(rawPayload, headers, cancellationToken)
                : await _processor.ProcessEventAsync(rawPayload, headers, cancellationToken);

            // Always 200/201 on successful receipt — Openserve should
            // never be given a reason to endlessly retry a payload we
            // already logged, even one we couldn't correlate.
            return StatusCode(201, new { result.Accepted, result.Message });
        }
        catch (Exception ex)
        {
            // The processor already swallows its own errors internally;
            // anything reaching here is a DI/infrastructure-level fault.
            // Still never 500 to Openserve — log loudly for us instead.
            _logger.LogError(ex, "[openserve][{Kind}][unhandled] processing threw outside the processor.", kind);
            return StatusCode(201, new { accepted = true, message = "Received; processing error logged for review." });
        }
    }
}

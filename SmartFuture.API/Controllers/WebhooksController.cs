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

    public WebhooksController(IWebhookInboxService service, IPayFastWebhookBridge payFastBridge)
    {
        _service = service;
        _payFastBridge = payFastBridge;
    }

    [HttpPost("payments/{providerName}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.WebhookPolicy)]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> ReceivePayment(string providerName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return ToActionResult(Result<WebhookInboxDto>.Failure(ErrorCodes.VALIDATION_ERROR, "providerName route value is required."));

        string rawPayload;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
        {
            rawPayload = await reader.ReadToEndAsync(cancellationToken);
        }

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
            var bridgeOutcome = await _payFastBridge.HandleAsync(
                rawPayload,
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

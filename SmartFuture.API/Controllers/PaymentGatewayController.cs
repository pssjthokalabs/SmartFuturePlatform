using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/payment-gateway")]
public class PaymentGatewayController : BaseController
{
    private readonly IPaymentGatewayService _service;
    private readonly IPaystackReconciliationService _paystackReconcile;
    private readonly IPaystackStatusService _paystackStatus;

    public PaymentGatewayController(
        IPaymentGatewayService service,
        IPaystackReconciliationService paystackReconcile,
        IPaystackStatusService paystackStatus)
    {
        _service = service;
        _paystackReconcile = paystackReconcile;
        _paystackStatus = paystackStatus;
    }

    [HttpPost("invoice/initiate")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> InitiateInvoicePayment([FromBody] InitiateInvoicePaymentRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.InitiateInvoicePaymentAsync(request, cancellationToken));

    /// <summary>
    /// Customer-facing verify-and-apply. Called by the portal's
    /// /payment/result page (and the mobile WebView return path) when
    /// the customer comes back from Paystack with outcome=success.
    /// Closes the loop when the webhook hasn't arrived yet.
    ///
    /// Anonymous BUT safe:
    ///   1. Reference must exist as a SmartFuture PaymentInitiation row
    ///      — attacker can't fabricate one.
    ///   2. Paystack /transaction/verify must confirm a PAID transaction
    ///      with matching amount + currency — attacker can't forge that.
    ///   3. PaymentApplierService is idempotent — replay is a no-op.
    ///   4. No PII is returned — only invoice number / status.
    /// </summary>
    [HttpPost("paystack/verify-and-apply")]
    [AllowAnonymous]
    public async Task<IActionResult> PaystackVerifyAndApply(
        [FromBody] PaystackVerifyAndApplyRequestDto request, CancellationToken cancellationToken)
    {
        request ??= new PaystackVerifyAndApplyRequestDto();
        return ToActionResult(await _paystackReconcile.ReconcileAsync(request.Reference ?? string.Empty, cancellationToken));
    }

    /// <summary>
    /// Read-only status check for a Paystack reference. Mobile + portal
    /// call this on a short poll (every few seconds for ~30s) after the
    /// first verify-and-apply so they can render "Order submitted" the
    /// moment the apply path finishes, without hammering Paystack's
    /// <c>/transaction/verify</c> endpoint on every tick.
    ///
    /// Anonymous BUT safe:
    ///   1. Reference is the auth — attacker must guess a valid
    ///      SF-INTENT-… or SF-PAY-… reference.
    ///   2. Response is a pure read of SmartFuture DB rows — no
    ///      Paystack call, no state mutation, no audit log.
    ///   3. No PII is returned — only invoice number / order number /
    ///      payment status. Same surface the verify-and-apply response
    ///      already exposes anonymously.
    /// </summary>
    [HttpGet("paystack/status")]
    [AllowAnonymous]
    public async Task<IActionResult> PaystackStatus(
        [FromQuery] string? reference, CancellationToken cancellationToken)
        => ToActionResult(await _paystackStatus.GetStatusAsync(reference ?? string.Empty, cancellationToken));

    /// <summary>
    /// Provider-agnostic alias for the status read above. The
    /// underlying service already handles SF-INTENT-… references for
    /// BOTH Paystack and PayFast intents (the OrderIntent lookup is
    /// not filtered by Provider), so this endpoint is just a cleaner
    /// URL for callers that aren't Paystack-specific.
    ///
    /// Use this from the mobile new-order PayFast path — calling the
    /// Paystack-named endpoint there worked but was misleading and
    /// surfaced in support tickets as "PayFast verified via Paystack
    /// endpoint". The verify-and-apply POST above stays Paystack-only
    /// (it does a real Paystack /transaction/verify); PayFast intents
    /// settle via the PayFast ITN webhook and only need to read
    /// status, never force-verify.
    /// </summary>
    [HttpGet("order-intent/status")]
    [AllowAnonymous]
    public async Task<IActionResult> OrderIntentStatus(
        [FromQuery] string? reference, CancellationToken cancellationToken)
        => ToActionResult(await _paystackStatus.GetStatusAsync(reference ?? string.Empty, cancellationToken));

    /// <summary>
    /// PayFast hosted-checkout return URL target. PayFast redirects the
    /// customer's browser here after a successful payment decision (it
    /// is NOT a webhook). We DO NOT mark anything paid from this hit —
    /// the PayFast ITN posted server-to-server is the source of truth.
    ///
    /// Renders a tiny self-closing HTML page that:
    /// — never requires orderId/invoiceId (intent payments don't have
    ///   either until the ITN materialises them);
    /// — surfaces the reference (m_payment_id) so a customer who reads
    ///   the page can quote it to support;
    /// — tells mobile customers to return to the SmartFuture app where
    ///   the result screen polls /order-intent/status.
    /// </summary>
    [HttpGet("return/payfast")]
    [AllowAnonymous]
    public IActionResult PayFastReturn([FromQuery(Name = "m_payment_id")] string? mPaymentId)
        => RenderPayFastBrowserPage(
            title: "Payment received",
            heading: "Payment received",
            body: "PayFast has received your payment. Please return to the SmartFuture app to finish your order. " +
                  "Your installation is confirmed only after we receive the final notification from PayFast — " +
                  "this is usually within a minute.",
            reference: mPaymentId);

    /// <summary>
    /// PayFast cancel URL target. Customer chose to cancel on the PayFast
    /// hosted-checkout page. No payment was taken. We do NOT mark
    /// anything failed from here — the ITN (if any) drives state.
    /// </summary>
    [HttpGet("cancel/payfast")]
    [AllowAnonymous]
    public IActionResult PayFastCancel([FromQuery(Name = "m_payment_id")] string? mPaymentId)
        => RenderPayFastBrowserPage(
            title: "Payment cancelled",
            heading: "Payment cancelled",
            body: "Your PayFast payment was cancelled. No money has been taken. " +
                  "Please return to the SmartFuture app to try again or pick a different payment method.",
            reference: mPaymentId);

    private ContentResult RenderPayFastBrowserPage(string title, string heading, string body, string? reference)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeHeading = WebUtility.HtmlEncode(heading);
        var safeBody = WebUtility.HtmlEncode(body);
        var safeRef = WebUtility.HtmlEncode(reference ?? string.Empty);

        // CSS uses '{' and '}' which clash with interpolated strings, so
        // the template is a plain raw string and values are concatenated.
        const string css = """
            <style>
              body{font-family:-apple-system,Segoe UI,Roboto,sans-serif;background:#0b0f14;color:#e6edf3;margin:0;display:flex;align-items:center;justify-content:center;min-height:100vh;padding:24px}
              .card{background:#161b22;border:1px solid #30363d;border-radius:12px;padding:28px 24px;max-width:480px;box-shadow:0 8px 24px rgba(0,0,0,.35)}
              h1{margin:0 0 12px;font-size:20px}
              p{margin:0 0 12px;line-height:1.5;color:#c9d1d9}
              .ref{font-family:Menlo,Consolas,monospace;background:#0d1117;border:1px solid #30363d;padding:8px 10px;border-radius:6px;display:inline-block;color:#7ee787;word-break:break-all}
              .muted{color:#8b949e;font-size:13px}
            </style>
            """;

        var refBlock = string.IsNullOrEmpty(safeRef)
            ? string.Empty
            : "<p class=\"muted\">Reference</p><p><span class=\"ref\">" + safeRef + "</span></p>";

        var html =
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\" />" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\" />" +
            "<title>" + safeTitle + " — SmartFuture</title>" + css + "</head>" +
            "<body><main class=\"card\">" +
            "<h1>" + safeHeading + "</h1>" +
            "<p>" + safeBody + "</p>" +
            refBlock +
            "<p class=\"muted\">You can safely close this window.</p>" +
            "</main></body></html>";

        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = 200,
        };
    }

    [HttpGet("admin/initiations")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] PaymentInitiationFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/initiations/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));
}

public class PaystackVerifyAndApplyRequestDto
{
    /// <summary>SF-PAY-… reference returned by the Paystack redirect (or webhook).</summary>
    public string? Reference { get; set; }
}

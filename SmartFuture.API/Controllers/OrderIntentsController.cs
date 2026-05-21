using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

// Phase 50B — public pre-order intent handoff.
//
// The marketing website POSTs to /api/public/order-intents anonymously
// when a visitor starts an order. The portal then redirects to register
// or login (passing the returned intentToken), and after authentication
// the order page calls claim + convert. The convert endpoint delegates
// to IOrderService.CreateMineAsync so backend pricing and snapshots
// stay authoritative — the website cannot influence the final price.
[ApiController]
public class OrderIntentsController : BaseController
{
    private readonly IOrderIntentService _service;

    public OrderIntentsController(IOrderIntentService service)
    {
        _service = service;
    }

    // ── Public (anonymous) endpoints ──────────────────────────────

    [HttpPost("api/public/order-intents")]
    [AllowAnonymous]
    public async Task<IActionResult> CreatePublic([FromBody] CreateOrderIntentRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreatePublicAsync(request, cancellationToken));

    // Phase 50D — website's full-registration entry point. Creates the
    // User + CustomerProfile + OrderIntent + one-time portal-auth-
    // handoff token in a single transaction so the website can land the
    // visitor directly on /client/auth/handoff and skip the portal
    // register screen entirely.
    [HttpPost("api/public/order-intents/register")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterAndCreate([FromBody] CreateOrderIntentWithRegistrationRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.RegisterAndCreateIntentAsync(request, cancellationToken));

    [HttpGet("api/public/order-intents/{intentToken}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublicPreview(string intentToken, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetPublicPreviewAsync(intentToken, cancellationToken));

    // ── Authenticated endpoints ───────────────────────────────────

    [HttpPost("api/order-intents/{intentToken}/claim")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Claim(string intentToken, CancellationToken cancellationToken)
        => ToActionResult(await _service.ClaimAsync(intentToken, cancellationToken));

    [HttpPost("api/order-intents/{intentToken}/convert")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Convert(string intentToken, [FromBody] ConvertOrderIntentRequestDto? overrides, CancellationToken cancellationToken)
        => ToActionResult(await _service.ConvertAsync(intentToken, overrides, cancellationToken));
}

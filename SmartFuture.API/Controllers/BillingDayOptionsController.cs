using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Billing;

namespace SmartFuture.API.Controllers;

// Public catalogue of enabled billing days. Consumed by the website
// pre-order modal, ClientZone checkout, and (future) mobile checkout to
// populate the "Choose your billing day" dropdown. No auth — the list
// is purely admin-curated.
[Route("api/billing-day-options")]
[AllowAnonymous]
public class BillingDayOptionsController : BaseController
{
    private readonly IBillingDayOptionService _service;

    public BillingDayOptionsController(IBillingDayOptionService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => ToActionResult(await _service.GetEnabledAsync(ct));
}

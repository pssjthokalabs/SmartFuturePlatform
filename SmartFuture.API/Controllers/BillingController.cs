using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Billing;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/billing")]
public class BillingController : BaseController
{
    private readonly IBillingOverviewService _overview;

    public BillingController(IBillingOverviewService overview)
    {
        _overview = overview;
    }

    // Phase 48 — single round-trip aggregator for the customer billing
    // dashboard. The /client/billing page used to stitch three separate
    // /mine endpoints together client-side; this hands the entire shape
    // back in one call so the page can render without N+1 fetches.
    [HttpGet("mine/overview")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMineOverview(CancellationToken cancellationToken)
        => ToActionResult(await _overview.GetMineAsync(cancellationToken));
}

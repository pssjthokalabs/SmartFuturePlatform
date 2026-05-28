using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Infrastructure.Configuration;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

// Phase 3.6 — read-only status surface so the portal can render the
// right banner ("not live yet", "disabled", "simulation") and gate
// action buttons without having to round-trip a 503 first.
//
// Auth: admin only. The status object is innocuous, but provisioning
// is an internal concern and we have no use case for surfacing it to
// customers yet.
[Route("api/provisioning/status")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class ProvisioningStatusController : BaseController
{
    private readonly IOptions<ProvisioningSettings> _options;

    public ProvisioningStatusController(IOptions<ProvisioningSettings> options)
    {
        _options = options;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var s = _options.Value;
        var label = !s.Enabled
            ? "Network provisioning is currently disabled."
            : s.Mode == ProvisioningMode.NoOp
                ? "Network provisioning is not live yet — simulation only."
                : $"Network provisioning mode '{s.Mode}' is configured but not yet honoured.";

        var dto = new ProvisioningStatusDto
        {
            Enabled = s.Enabled,
            Mode = s.Mode.ToString(),
            IsActionable = s.IsNoOpActionable,
            StatusLabel = label
        };
        return ToActionResult(Result<ProvisioningStatusDto>.Success(dto));
    }
}

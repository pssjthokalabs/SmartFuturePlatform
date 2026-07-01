using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

[Route("api/admin/coverage-map-rules")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class AdminCoverageMapController : BaseController
{
    private readonly ICoverageMapRuleService _service;

    public AdminCoverageMapController(ICoverageMapRuleService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => ToActionResult(await _service.GetAllAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => ToActionResult(await _service.GetByIdAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertCoverageMapRuleRequestDto request, CancellationToken ct)
        => ToActionResult(await _service.CreateAsync(request, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertCoverageMapRuleRequestDto request, CancellationToken ct)
        => ToActionResult(await _service.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
        => ToActionResult(await _service.SetActiveAsync(id, true, ct));

    [HttpPost("{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
        => ToActionResult(await _service.SetActiveAsync(id, false, ct));

    // Admin "would this address match?" probe. Reuses the same evaluator
    // the public coverage endpoint uses so admins can dry-run before
    // enabling a new rule.
    [HttpPost("test-match")]
    public async Task<IActionResult> TestMatch([FromBody] CoverageMapTestMatchRequestDto request, CancellationToken ct)
    {
        var evaluation = await _service.TryEvaluateAsync(request?.Address ?? new CoverageCheckRequestDto(), ct);
        var response = new CoverageMapTestMatchResponseDto
        {
            Evaluation = evaluation,
            SuggestedOutcome = evaluation.Matched
                ? (evaluation.MatchedType == Shared.Enums.Coverage.CoverageMapRuleType.Include
                    ? "Coverage would be available (admin include rule)."
                    : "Coverage would be blocked (admin exclude rule).")
                : "No admin rule would trip — Openserve would be consulted."
        };
        return ToActionResult(Result<CoverageMapTestMatchResponseDto>.Success(response));
    }
}

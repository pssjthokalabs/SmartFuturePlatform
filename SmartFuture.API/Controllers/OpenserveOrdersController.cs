using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

// Admin visibility + operations over the Openserve fulfilment pipeline
// (brief Priority 8). Read-heavy surface backed by
// IOpenserveOrderSubmissionService (Phase 1/2 records) and write
// actions backed by IOpenserveOrderSubmissionService.AdminRetrySubmissionAsync
// / IOpenserveReconciliationService (Synchronize now / Cancel) — no
// action here can double-submit: retry reuses the existing
// ExternalReferenceNumber/SubscriberReferenceNumber, and synchronize/
// cancel operate on an already-known Openserve order id.
[Route("api/openserve/admin/orders")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class OpenserveOrdersController : BaseController
{
    private readonly IOpenserveOrderSubmissionService _submissionService;
    private readonly IOpenserveReconciliationService _reconciliationService;
    private readonly IOpenserveOrderFulfilmentService _fulfilmentService;

    public OpenserveOrdersController(IOpenserveOrderSubmissionService submissionService, IOpenserveReconciliationService reconciliationService, IOpenserveOrderFulfilmentService fulfilmentService)
    {
        _submissionService = submissionService;
        _reconciliationService = reconciliationService;
        _fulfilmentService = fulfilmentService;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] OpenserveOrderFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _submissionService.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _submissionService.GetByIdAsync(id, cancellationToken));

    [HttpGet("by-order/{orderId:guid}")]
    public async Task<IActionResult> GetByOrderId(Guid orderId, CancellationToken cancellationToken)
        => ToActionResult(await _submissionService.GetByOrderIdAsync(orderId, cancellationToken));

    // ─── Admin Order Detail → "OPENserve Fulfilment" (by SmartFuture order id) ───

    [HttpGet("by-order/{orderId:guid}/fulfilment")]
    public async Task<IActionResult> GetFulfilment(Guid orderId, CancellationToken cancellationToken)
        => ToActionResult(await _fulfilmentService.GetAsync(orderId, cancellationToken));

    // Send to Openserve / Retry Openserve Submission. The portal only names the
    // order — the backend builds and sends the request through the same
    // coordinator as the automatic trigger and the recovery worker.
    [HttpPost("by-order/{orderId:guid}/submit")]
    public async Task<IActionResult> Submit(Guid orderId, [FromBody] SubmitOpenserveOrderRequestDto? request, CancellationToken cancellationToken)
        => ToActionResult(await _fulfilmentService.SubmitAsync(orderId, request?.ConfirmOutcomeUnknown ?? false, cancellationToken));

    // Product Qualification only: stores AMID/building data and refreshes
    // eligibility. Never sends the order; Admin Sends/Retries afterwards.
    [HttpPost("by-order/{orderId:guid}/qualify")]
    public async Task<IActionResult> RunQualification(Guid orderId, CancellationToken cancellationToken)
        => ToActionResult(await _fulfilmentService.RunQualificationAsync(orderId, cancellationToken));

    // MDU: Admin picks the building/unit from the rows Openserve returned (only
    // a returned BLD_NUM_ID is accepted). Never sends the order.
    [HttpPost("by-order/{orderId:guid}/building-unit")]
    public async Task<IActionResult> SelectBuildingUnit(Guid orderId, [FromBody] SelectOpenserveBuildingUnitRequestDto? request, CancellationToken cancellationToken)
        => ToActionResult(await _fulfilmentService.SelectBuildingUnitAsync(orderId, request?.BldNumId, cancellationToken));

    // Reload building/unit rows for the order's existing AMID (orders qualified
    // before the rows were stored). Never changes the AMID, never sends.
    [HttpPost("by-order/{orderId:guid}/building-candidates/refresh")]
    public async Task<IActionResult> RefreshBuildingCandidates(Guid orderId, CancellationToken cancellationToken)
        => ToActionResult(await _fulfilmentService.RefreshBuildingCandidatesAsync(orderId, cancellationToken));

    // Address review: Admin confirms (with a note) that the address Openserve
    // resolved for the AMID is the customer's property. Audited. Never sends
    // the order and never overrides Fibre/product availability.
    [HttpPost("by-order/{orderId:guid}/address-review/accept")]
    public async Task<IActionResult> AcceptAddress(Guid orderId, [FromBody] AcceptOpenserveAddressRequestDto? request, CancellationToken cancellationToken)
        => ToActionResult(await _fulfilmentService.AcceptAddressAsync(orderId, request?.Note, cancellationToken));

    [HttpPost("by-order/{orderId:guid}/pause-automation")]
    public async Task<IActionResult> PauseAutomation(Guid orderId, [FromBody] OpenserveAutomationPauseRequestDto? request, CancellationToken cancellationToken)
        => ToActionResult(await _fulfilmentService.PauseAutomationAsync(orderId, request?.Reason, cancellationToken));

    [HttpPost("by-order/{orderId:guid}/resume-automation")]
    public async Task<IActionResult> ResumeAutomation(Guid orderId, [FromBody] OpenserveAutomationPauseRequestDto? request, CancellationToken cancellationToken)
        => ToActionResult(await _fulfilmentService.ResumeAutomationAsync(orderId, request?.Reason, cancellationToken));

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> GetHistory(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _submissionService.GetHistoryAsync(id, cancellationToken));

    [HttpGet("{id:guid}/integration-logs")]
    public async Task<IActionResult> GetIntegrationLogs(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _submissionService.GetIntegrationLogsAsync(id, cancellationToken));

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, [FromBody] SubmitOpenserveOrderRequestDto? request, CancellationToken cancellationToken)
        => ToActionResult(await _submissionService.AdminRetrySubmissionAsync(id, request?.ConfirmOutcomeUnknown ?? false, cancellationToken));

    [HttpPost("{id:guid}/synchronize")]
    public async Task<IActionResult> Synchronize(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _reconciliationService.SynchronizeNowAsync(id, isManualTrigger: true, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _reconciliationService.AdminCancelOrderAsync(id, cancellationToken));
}

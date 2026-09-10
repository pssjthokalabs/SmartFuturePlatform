using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Openserve;
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

    public OpenserveOrdersController(
        IOpenserveOrderSubmissionService submissionService, IOpenserveReconciliationService reconciliationService)
    {
        _submissionService = submissionService;
        _reconciliationService = reconciliationService;
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

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> GetHistory(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _submissionService.GetHistoryAsync(id, cancellationToken));

    [HttpGet("{id:guid}/integration-logs")]
    public async Task<IActionResult> GetIntegrationLogs(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _submissionService.GetIntegrationLogsAsync(id, cancellationToken));

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _submissionService.AdminRetrySubmissionAsync(id, cancellationToken));

    [HttpPost("{id:guid}/synchronize")]
    public async Task<IActionResult> Synchronize(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _reconciliationService.SynchronizeNowAsync(id, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _reconciliationService.AdminCancelOrderAsync(id, cancellationToken));
}

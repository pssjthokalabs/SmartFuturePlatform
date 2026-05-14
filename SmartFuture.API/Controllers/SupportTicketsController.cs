using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.SupportTickets;
using SmartFuture.Application.SupportTickets.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/support-tickets")]
public class SupportTicketsController : BaseController
{
    private readonly ISupportTicketService _service;

    public SupportTicketsController(ISupportTicketService service)
    {
        _service = service;
    }

    [HttpGet("mine")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMine([FromQuery] SupportTicketFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineAsync(filter, cancellationToken));

    [HttpGet("mine/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMineById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetMineByIdAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Create([FromBody] CreateSupportTicketRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.CreateMineAsync(request, cancellationToken));

    [HttpPost("mine/{id:guid}/close")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> Close(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.CloseMineAsync(id, cancellationToken));

    [HttpGet("mine/{id:guid}/comments")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> GetMineComments(Guid id, [FromQuery] SupportTicketCommentFilterRequestDto filter, CancellationToken cancellationToken)
    {
        filter ??= new SupportTicketCommentFilterRequestDto();
        filter.SupportTicketId = id;
        return ToActionResult(await _service.GetCommentsMineAsync(filter, cancellationToken));
    }

    [HttpPost("mine/{id:guid}/comments")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> AddMineComment(Guid id, [FromBody] AddSupportTicketCommentRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AddCommentMineAsync(id, request, cancellationToken));

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> SearchAdmin([FromQuery] SupportTicketFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPut("admin/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminUpdate(Guid id, [FromBody] AdminUpdateSupportTicketRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminUpdateAsync(id, request, cancellationToken));

    [HttpPost("admin/{id:guid}/status")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminUpdateStatus(Guid id, [FromBody] AdminUpdateSupportTicketStatusDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AdminUpdateStatusAsync(id, request, cancellationToken));

    [HttpPost("admin/{id:guid}/assign")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AdminAssign(Guid id, [FromBody] AssignSupportTicketRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AssignAsync(id, request, cancellationToken));

    [HttpGet("admin/{id:guid}/comments")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> GetAdminComments(Guid id, [FromQuery] SupportTicketCommentFilterRequestDto filter, CancellationToken cancellationToken)
    {
        filter ??= new SupportTicketCommentFilterRequestDto();
        filter.SupportTicketId = id;
        return ToActionResult(await _service.GetCommentsAdminAsync(filter, cancellationToken));
    }

    [HttpPost("admin/{id:guid}/comments")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
    public async Task<IActionResult> AddAdminComment(Guid id, [FromBody] AddSupportTicketCommentRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.AddCommentAdminAsync(id, request, cancellationToken));
}

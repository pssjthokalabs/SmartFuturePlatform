using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Shared.Constants;

namespace SmartFuture.API.Controllers;

[Route("api/notifications")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class NotificationsController : BaseController
{
    private readonly INotificationService _service;

    public NotificationsController(INotificationService service)
    {
        _service = service;
    }

    [HttpGet("admin")]
    public async Task<IActionResult> SearchAdmin([FromQuery] NotificationFilterRequestDto filter, CancellationToken cancellationToken)
        => ToActionResult(await _service.SearchAdminAsync(filter, cancellationToken));

    [HttpGet("admin/{id:guid}")]
    public async Task<IActionResult> GetAdmin(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await _service.GetAdminByIdAsync(id, cancellationToken));

    [HttpPost("admin/send")]
    public async Task<IActionResult> Send([FromBody] SendNotificationRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _service.SendAsync(request, cancellationToken));
}

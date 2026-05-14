using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Notifications;

public interface INotificationService
{
    Task<Result<OutboundNotificationDto>> SendAsync(
        SendNotificationRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<OutboundNotificationDto>>> SearchAdminAsync(
        NotificationFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<OutboundNotificationDto>> GetAdminByIdAsync(
        Guid id, CancellationToken cancellationToken = default);
}

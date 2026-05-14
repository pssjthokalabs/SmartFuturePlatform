using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Auditing.Dtos;

public class AuditLogFilterRequestDto : PagedListQueryBase
{
    public Guid? ActorUserId { get; set; }
    public AuditActorType? ActorType { get; set; }
    public AuditActionType? ActionType { get; set; }
    public AuditEntityType? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public bool? IsSuccess { get; set; }
}

using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Auditing;

namespace SmartFuture.Domain.Auditing;

public class AuditLog : BaseEntity
{
    public Guid? ActorUserId { get; set; }
    public User? ActorUser { get; set; }

    public AuditActorType ActorType { get; set; }
    public AuditActionType ActionType { get; set; }
    public AuditEntityType EntityType { get; set; }

    public Guid? EntityId { get; set; }
    public string? EntityName { get; set; }
    public string? Summary { get; set; }
    public string? MetadataJson { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public bool IsSuccess { get; set; } = true;
    public string? FailureReason { get; set; }
}

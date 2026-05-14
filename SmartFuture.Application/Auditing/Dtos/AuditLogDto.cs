using SmartFuture.Shared.Enums.Auditing;

namespace SmartFuture.Application.Auditing.Dtos;

public class AuditLogDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Guid? ActorUserId { get; set; }
    public string? ActorUserEmail { get; set; }
    public string? ActorUserFullName { get; set; }

    public AuditActorType ActorType { get; set; }
    public AuditActionType ActionType { get; set; }
    public AuditEntityType EntityType { get; set; }

    public Guid? EntityId { get; set; }
    public string? EntityName { get; set; }
    public string? Summary { get; set; }
    public string? MetadataJson { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public bool IsSuccess { get; set; }
    public string? FailureReason { get; set; }
}

using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Privacy;

namespace SmartFuture.Domain.Privacy;

public class PrivacyRequest : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public PrivacyRequestType Type { get; set; } = PrivacyRequestType.DataErasure;
    public PrivacyRequestStatus Status { get; set; } = PrivacyRequestStatus.Submitted;

    public string? RequestReason { get; set; }
    public string? AdminNotes { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public User? CompletedByUser { get; set; }
}

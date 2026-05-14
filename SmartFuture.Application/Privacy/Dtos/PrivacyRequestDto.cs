using SmartFuture.Shared.Enums.Privacy;

namespace SmartFuture.Application.Privacy.Dtos;

public class PrivacyRequestDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? UserEmail { get; set; }
    public PrivacyRequestType Type { get; set; }
    public PrivacyRequestStatus Status { get; set; }
    public string? RequestReason { get; set; }
    public string? AdminNotes { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewedByUserEmail { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public string? CompletedByUserEmail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

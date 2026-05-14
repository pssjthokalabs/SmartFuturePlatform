using SmartFuture.Shared.Enums.Privacy;

namespace SmartFuture.Application.Privacy.Dtos;

public class AdminUpdatePrivacyRequestStatusDto
{
    public PrivacyRequestStatus Status { get; set; }
    public string? AdminNotes { get; set; }
    public string? RejectionReason { get; set; }
}

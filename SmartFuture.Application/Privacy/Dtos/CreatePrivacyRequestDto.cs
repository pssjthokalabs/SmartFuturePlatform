using SmartFuture.Shared.Enums.Privacy;

namespace SmartFuture.Application.Privacy.Dtos;

public class CreatePrivacyRequestDto
{
    public PrivacyRequestType Type { get; set; } = PrivacyRequestType.DataErasure;
    public string? RequestReason { get; set; }
}

namespace SmartFuture.Application.Auth.Dtos;

public class RequestChangePasswordCodeRequestDto
{
    // "email" | "sms". Defaults to email on the backend when omitted.
    public string? Channel { get; set; }
}

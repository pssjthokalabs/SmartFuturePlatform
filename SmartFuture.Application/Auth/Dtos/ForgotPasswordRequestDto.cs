namespace SmartFuture.Application.Auth.Dtos;

public class ForgotPasswordRequestDto
{
    public string Email { get; set; } = string.Empty;

    public string? Portal { get; set; }
}

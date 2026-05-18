namespace SmartFuture.Application.Auth.Dtos;

public class ConfirmChangePasswordRequestDto
{
    public string Code { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

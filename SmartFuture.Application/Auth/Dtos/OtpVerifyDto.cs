namespace SmartFuture.Application.Auth.Dtos;

public class OtpVerifyDto
{
    public string Identifier { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

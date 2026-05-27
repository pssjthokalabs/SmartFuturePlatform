namespace SmartFuture.Application.Auth.Dtos;

public class OtpRequestDto
{
    public string Identifier { get; set; } = string.Empty;
    public string Channel { get; set; } = "sms";
}

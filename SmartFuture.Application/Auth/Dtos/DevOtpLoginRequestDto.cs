namespace SmartFuture.Application.Auth.Dtos;

/// <summary>
/// Request body for the TEMPORARY dev / UAT OTP-login endpoint.
///
/// REMOVE this DTO + the matching <see cref="IAuthService.DevOtpLoginAsync"/>
/// method + the AuthController action when a real OTP delivery provider is
/// integrated. The endpoint is gated to non-production environments inside
/// the service so it can never authenticate against Live, but the cleanest
/// removal is also dropping the surface entirely.
/// </summary>
public class DevOtpLoginRequestDto
{
    /// <summary>Email or phone number identifying the user.</summary>
    public string EmailOrPhone { get; set; } = string.Empty;

    /// <summary>The one-time pin entered by the user.</summary>
    public string Otp { get; set; } = string.Empty;
}

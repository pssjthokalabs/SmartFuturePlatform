namespace SmartFuture.Application.Auth.Dtos;

// Direct, current-password-verified password change for the logged-in
// user (admin Settings page + any "change my password" surface). Unlike
// the OTP-based change-password flow (request-code / confirm), this does
// NOT require a one-time code — the caller proves identity by supplying
// their CURRENT password, which Identity's ChangePasswordAsync verifies.
public class ChangePasswordRequestDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

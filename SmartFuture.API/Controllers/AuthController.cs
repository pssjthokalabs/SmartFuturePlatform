using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartFuture.API.Extensions;
using SmartFuture.Application.Auth;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

[Route("api/auth")]
[EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
public class AuthController : BaseController
{
    private readonly IAuthService _authService;
    private readonly IPortalAuthHandoffService _portalHandoffService;
    private readonly ICurrentUserService _currentUser;

    public AuthController(IAuthService authService, IPortalAuthHandoffService portalHandoffService, ICurrentUserService currentUser)
    {
        _authService = authService;
        _portalHandoffService = portalHandoffService;
        _currentUser = currentUser;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
        => ToActionResult(await _authService.RegisterAsync(request));

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
        => ToActionResult(await _authService.LoginAsync(request));

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequestDto request)
        => ToActionResult(await _authService.RefreshTokenAsync(request));

    // Phase 50D — exchanges a one-time portal-auth-handoff token (issued
    // by the website's register-and-create-intent endpoint) for a normal
    // login response. The portal hits this directly from
    // /client/auth/handoff so the raw token never lands in API access
    // logs (it stays in the request body, never the URL).
    [HttpPost("portal-handoff/exchange")]
    [AllowAnonymous]
    public async Task<IActionResult> PortalHandoffExchange([FromBody] PortalAuthHandoffExchangeRequestDto request, CancellationToken cancellationToken)
        => ToActionResult(await _portalHandoffService.ExchangeAsync(request, cancellationToken));

    [HttpPost("revoke-refresh-token")]
    [Authorize]
    public async Task<IActionResult> Revoke([FromBody] RefreshTokenRequestDto request)
        => ToActionResult(await _authService.RevokeRefreshTokenAsync(request));

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequestDto request)
        => ToActionResult(await _authService.ForgotPasswordAsync(request));

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequestDto request)
        => ToActionResult(await _authService.ResetPasswordAsync(request));

    [HttpPost("change-password/request-code")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> RequestChangePasswordCode([FromBody] RequestChangePasswordCodeRequestDto request)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));
        return ToActionResult(await _authService.RequestChangePasswordCodeAsync(_currentUser.UserId.Value, request));
    }

    [HttpPost("change-password/confirm")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> ConfirmChangePassword([FromBody] ConfirmChangePasswordRequestDto request)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));
        return ToActionResult(await _authService.ConfirmChangePasswordAsync(_currentUser.UserId.Value, request));
    }

    // Direct password change (no OTP) for the logged-in user — used by the
    // admin Settings page. Identity verifies the supplied current password
    // and enforces the password policy. The id comes from the JWT, so a
    // user can only ever change their OWN password.
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequestDto request)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));
        return ToActionResult(await _authService.ChangePasswordAsync(_currentUser.UserId.Value, request));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result<CurrentUserDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return ToActionResult(await _authService.GetCurrentUserAsync(_currentUser.UserId.Value));
    }

    // Phase 51 — Registration availability probes for the mobile signup
    // wizard. Both return only a boolean availability flag so the surface
    // can't be abused as a profile-enumeration oracle.
    [HttpGet("check-email")]
    [AllowAnonymous]
    public async Task<IActionResult> CheckEmail([FromQuery] string email)
        => ToActionResult(await _authService.IsEmailAvailableAsync(email));

    [HttpGet("check-phone")]
    [AllowAnonymous]
    public async Task<IActionResult> CheckPhone([FromQuery] string phoneNumber)
        => ToActionResult(await _authService.IsPhoneAvailableAsync(phoneNumber));

    // TEMPORARY dev / UAT OTP login bridge. Hard-gated to non-production
    // inside IAuthService.DevOtpLoginAsync. See the removal checklist in
    // AuthService.DevOtpLoginAsync when a real OTP provider ships.
    [HttpPost("dev-otp-login")]
    [AllowAnonymous]
    public async Task<IActionResult> DevOtpLogin([FromBody] DevOtpLoginRequestDto request)
        => ToActionResult(await _authService.DevOtpLoginAsync(request));

    [HttpPost("otp/request")]
    [AllowAnonymous]
    public async Task<IActionResult> RequestOtp([FromBody] OtpRequestDto request)
        => ToActionResult(await _authService.RequestOtpAsync(request));

    [HttpPost("otp/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyOtp([FromBody] OtpVerifyDto request)
        => ToActionResult(await _authService.VerifyOtpAsync(request));

    // Post-registration account verification. Both endpoints are
    // authenticated — the JWT carries the user the OTP should target.
    // Removes any account-enumeration risk that an unauth'd request
    // body would carry.
    [HttpPost("verify-account/request-code")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> RequestAccountVerificationCode([FromBody] RequestAccountVerificationCodeDto request)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));
        return ToActionResult(await _authService.RequestAccountVerificationCodeAsync(_currentUser.UserId.Value, request));
    }

    [HttpPost("verify-account/confirm")]
    [Authorize(Policy = AuthorizationPolicies.RequireActiveUser)]
    public async Task<IActionResult> ConfirmAccountVerification([FromBody] ConfirmAccountVerificationDto request)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result<AccountVerificationStatusDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));
        return ToActionResult(await _authService.ConfirmAccountVerificationAsync(_currentUser.UserId.Value, request));
    }

    // Pre-login / no-session counterparts. Used when the customer's
    // last login attempt returned ACCOUNT_VERIFICATION_REQUIRED — the
    // portal has no JWT yet, so the authenticated endpoints above are
    // unreachable. Anonymous BUT inheriting the controller-level
    // AuthPolicy rate limit so brute force is bounded the same as
    // /api/auth/login.
    [HttpPost("verify-account-public/request-code")]
    [AllowAnonymous]
    public async Task<IActionResult> PublicRequestAccountVerificationCode([FromBody] PublicRequestAccountVerificationCodeDto request)
        => ToActionResult(await _authService.PublicRequestAccountVerificationCodeAsync(request));

    [HttpPost("verify-account-public/confirm")]
    [AllowAnonymous]
    public async Task<IActionResult> PublicConfirmAccountVerification([FromBody] PublicConfirmAccountVerificationDto request)
        => ToActionResult(await _authService.PublicConfirmAccountVerificationAsync(request));
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartFuture.API.Extensions;
using SmartFuture.Application.Auth;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

[Route("api/auth")]
[EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
public class AuthController : BaseController
{
    private readonly IAuthService _authService;
    private readonly ICurrentUserService _currentUser;

    public AuthController(IAuthService authService, ICurrentUserService currentUser)
    {
        _authService = authService;
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

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is null)
            return ToActionResult(Result<CurrentUserDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return ToActionResult(await _authService.GetCurrentUserAsync(_currentUser.UserId.Value));
    }
}

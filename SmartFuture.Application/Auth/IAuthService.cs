using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Auth;

public interface IAuthService
{
    Task<Result<AuthTokenDto>> RegisterAsync(RegisterRequestDto request);
    Task<Result<AuthTokenDto>> LoginAsync(LoginRequestDto request);
    Task<Result<AuthTokenDto>> RefreshTokenAsync(RefreshTokenRequestDto request);
    Task<Result> RevokeRefreshTokenAsync(RefreshTokenRequestDto request);
    Task<Result> ForgotPasswordAsync(ForgotPasswordRequestDto request);
    Task<Result> ResetPasswordAsync(ResetPasswordRequestDto request);
    Task<Result> RequestChangePasswordCodeAsync(Guid userId, RequestChangePasswordCodeRequestDto request);
    Task<Result> ConfirmChangePasswordAsync(Guid userId, ConfirmChangePasswordRequestDto request);
    Task<Result<CurrentUserDto>> GetCurrentUserAsync(Guid userId);
}

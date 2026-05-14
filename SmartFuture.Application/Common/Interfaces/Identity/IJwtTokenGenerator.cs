using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Domain.Identity;

namespace SmartFuture.Application.Common.Interfaces.Identity;

public interface IJwtTokenGenerator
{
    Task<AuthTokenDto> GenerateTokenAsync(User user);
    Task<AuthTokenDto?> RefreshTokenAsync(string refreshToken);
    Task<bool> RevokeRefreshTokenAsync(string refreshToken);
    Task RevokeAllRefreshTokensForUserAsync(Guid userId);
}

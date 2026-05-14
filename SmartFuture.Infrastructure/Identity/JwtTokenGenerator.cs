using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Identity;
using SmartFuture.Infrastructure.Configuration;

namespace SmartFuture.Infrastructure.Identity;

public class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly UserManager<User> _userManager;
    private readonly IAppDbContext _dbContext;
    private readonly JwtSettings _settings;

    public JwtTokenGenerator(UserManager<User> userManager, IAppDbContext dbContext, IOptions<JwtSettings> settings)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _settings = settings.Value;
    }

    public async Task<AuthTokenDto> GenerateTokenAsync(User user)
    {
        var (accessToken, accessExpires) = await BuildAccessTokenAsync(user);
        var refresh = await CreateAndPersistRefreshTokenAsync(user.Id);

        var roles = await _userManager.GetRolesAsync(user);

        return new AuthTokenDto
        {
            AccessToken = accessToken,
            RefreshToken = refresh.Token,
            AccessTokenExpiresAtUtc = accessExpires,
            RefreshTokenExpiresAtUtc = refresh.ExpiresAtUtc,
            User = new CurrentUserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email ?? string.Empty,
                PhoneNumber = user.PhoneNumber,
                AccountStatus = user.AccountStatus.ToString(),
                IsActive = user.IsActive,
                Roles = roles.ToList()
            }
        };
    }

    public async Task<AuthTokenDto?> RefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        var existing = await _dbContext.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken);

        if (existing is null) return null;
        if (existing.RevokedAtUtc != null) return null;
        if (DateTime.UtcNow >= existing.ExpiresAtUtc) return null;
        if (existing.User is null) return null;

        var now = DateTime.UtcNow;
        var rotated = await CreateAndPersistRefreshTokenAsync(existing.UserId);

        existing.RevokedAtUtc = now;
        existing.ReplacedByToken = rotated.Token;

        await _dbContext.SaveChangesAsync();

        var (accessToken, accessExpires) = await BuildAccessTokenAsync(existing.User);
        var roles = await _userManager.GetRolesAsync(existing.User);

        return new AuthTokenDto
        {
            AccessToken = accessToken,
            RefreshToken = rotated.Token,
            AccessTokenExpiresAtUtc = accessExpires,
            RefreshTokenExpiresAtUtc = rotated.ExpiresAtUtc,
            User = new CurrentUserDto
            {
                Id = existing.User.Id,
                FirstName = existing.User.FirstName,
                LastName = existing.User.LastName,
                Email = existing.User.Email ?? string.Empty,
                PhoneNumber = existing.User.PhoneNumber,
                AccountStatus = existing.User.AccountStatus.ToString(),
                IsActive = existing.User.IsActive,
                Roles = roles.ToList()
            }
        };
    }

    public async Task<bool> RevokeRefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return false;

        var existing = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken);

        if (existing is null || existing.RevokedAtUtc != null) return false;

        existing.RevokedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task RevokeAllRefreshTokensForUserAsync(Guid userId)
    {
        var tokens = await _dbContext.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAtUtc == null)
            .ToListAsync();

        if (tokens.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var t in tokens)
            t.RevokedAtUtc = now;

        await _dbContext.SaveChangesAsync();
    }

    private async Task<(string Token, DateTime ExpiresAtUtc)> BuildAccessTokenAsync(User user)
    {
        var roles = await _userManager.GetRolesAsync(user);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.GivenName, user.FirstName ?? string.Empty),
            new(ClaimTypes.Surname, user.LastName ?? string.Empty),
            new("user_id", user.Id.ToString()),
            new("account_status", user.AccountStatus.ToString()),
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var keyBytes = Encoding.UTF8.GetBytes(_settings.Key);
        var signingKey = new SymmetricSecurityKey(keyBytes);
        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var expires = DateTime.UtcNow.AddMinutes(_settings.AccessTokenMinutes);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires,
            signingCredentials: creds);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        return (jwt, expires);
    }

    private async Task<RefreshToken> CreateAndPersistRefreshTokenAsync(Guid userId)
    {
        var bytes = new byte[64];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);

        var tokenValue = Convert.ToBase64String(bytes);

        var refresh = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Token = tokenValue,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(_settings.RefreshTokenDays)
        };

        _dbContext.RefreshTokens.Add(refresh);
        await _dbContext.SaveChangesAsync();
        return refresh;
    }
}

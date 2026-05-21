using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Auth;
using SmartFuture.Shared.Enums.Auth;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Auth;

// Phase 50D — owns the lifecycle of PortalAuthHandoffToken rows.
//
// Issue path: the website's register-and-create-intent flow asks us
// for a single-use token immediately after creating the user. We
// generate 32 bytes of CSPRNG entropy, store its SHA-256 hash, and
// return the raw token **once** to the caller.
//
// Exchange path: the portal hashes the token it received in the
// handoff URL and looks the row up. We reject expired or already-
// consumed tokens, mark the row consumed, and ask IJwtTokenGenerator
// for the same AuthTokenDto a /api/auth/login call would have
// produced — so downstream session handling is identical.
//
// The raw token is never logged. The IntentToken passed through the
// handoff is logged because it is itself non-secret (already required
// to be in the URL the user just used).
public class PortalAuthHandoffService : IPortalAuthHandoffService
{
    private readonly IAppDbContext _dbContext;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<PortalAuthHandoffService> _logger;

    public PortalAuthHandoffService(IAppDbContext dbContext, IJwtTokenGenerator jwtTokenGenerator, ICurrentUserService currentUser, ILogger<PortalAuthHandoffService> logger)
    {
        _dbContext = dbContext;
        _jwtTokenGenerator = jwtTokenGenerator;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<IssuedHandoffToken> IssueAsync(Guid userId, string? intentToken, PortalAuthHandoffPurpose purpose, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentException("Lifetime must be positive.", nameof(lifetime));

        var rawToken = GenerateRawToken();
        var hash = ComputeHash(rawToken);
        var now = DateTime.UtcNow;

        var entity = new PortalAuthHandoffToken
        {
            TokenHash = hash,
            UserId = userId,
            IntentToken = string.IsNullOrWhiteSpace(intentToken) ? null : intentToken.Trim(),
            Purpose = purpose,
            ExpiresAtUtc = now.Add(lifetime),
            CreatedIpAddress = Trunc(_currentUser.IpAddress, 64),
            CreatedUserAgent = Trunc(_currentUser.UserAgent, 512),
        };

        _dbContext.PortalAuthHandoffTokens.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Issued portal-auth-handoff token {TokenId} for user {UserId} (expires {ExpiresAtUtc:o}).",
            entity.Id, userId, entity.ExpiresAtUtc);

        return new IssuedHandoffToken(rawToken, entity.Id, entity.ExpiresAtUtc);
    }

    public async Task<Result<PortalAuthHandoffExchangeResponseDto>> ExchangeAsync(PortalAuthHandoffExchangeRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Token))
                return Result<PortalAuthHandoffExchangeResponseDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Handoff token is required.");

            var hash = ComputeHash(request.Token.Trim());
            var row = await _dbContext.PortalAuthHandoffTokens
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

            if (row is null)
                return Result<PortalAuthHandoffExchangeResponseDto>.Failure(
                    ErrorCodes.UNAUTHORIZED,
                    "This sign-in link is no longer valid. Please sign in manually.");

            if (row.ConsumedAtUtc is not null)
            {
                _logger.LogInformation(
                    "Portal-auth-handoff token {TokenId} already consumed at {ConsumedAtUtc:o}; rejecting replay.",
                    row.Id, row.ConsumedAtUtc);
                return Result<PortalAuthHandoffExchangeResponseDto>.Failure(
                    ErrorCodes.UNAUTHORIZED,
                    "This sign-in link has already been used. Please sign in manually.");
            }

            if (row.ExpiresAtUtc <= DateTime.UtcNow)
                return Result<PortalAuthHandoffExchangeResponseDto>.Failure(
                    ErrorCodes.UNAUTHORIZED,
                    "This sign-in link has expired. Please sign in manually.");

            var user = row.User;
            if (user is null)
                return Result<PortalAuthHandoffExchangeResponseDto>.Failure(
                    ErrorCodes.UNAUTHORIZED,
                    "This sign-in link is no longer valid. Please sign in manually.");

            if (!user.IsActive
                || user.AccountStatus == UserAccountStatus.Suspended
                || user.AccountStatus == UserAccountStatus.Inactive)
            {
                return Result<PortalAuthHandoffExchangeResponseDto>.Failure(
                    ErrorCodes.FORBIDDEN, "This account cannot sign in.");
            }

            // Consume *before* generating the auth token. Even if the JWT
            // step somehow throws after this, we'd rather a user re-do
            // the registration than have a valid replayable token sitting
            // around. SaveChanges is atomic for a single-row update.
            row.ConsumedAtUtc = DateTime.UtcNow;
            row.ConsumedIpAddress = Trunc(_currentUser.IpAddress, 64);
            row.ConsumedUserAgent = Trunc(_currentUser.UserAgent, 512);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var authToken = await _jwtTokenGenerator.GenerateTokenAsync(user);

            _logger.LogInformation(
                "Consumed portal-auth-handoff token {TokenId} for user {UserId}.", row.Id, user.Id);

            return Result<PortalAuthHandoffExchangeResponseDto>.Success(
                new PortalAuthHandoffExchangeResponseDto
                {
                    Auth = authToken,
                    IntentToken = string.IsNullOrWhiteSpace(request.IntentToken)
                        ? row.IntentToken
                        : request.IntentToken,
                },
                "Signed in.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error exchanging portal-auth-handoff token.");
            return Result<PortalAuthHandoffExchangeResponseDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while signing you in.");
        }
    }

    // ───────────────────────── crypto helpers ─────────────────────────

    // 32 bytes → 256 bits of entropy, base64-url encoded for safe use in
    // a URL query string (no padding, no '+' or '/').
    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // SHA-256 hash, hex-encoded. We never store the raw token, so a DB
    // dump cannot replay handoffs. Hex over base64 because it's
    // length-stable and case-insensitive comparisons are easier.
    private static string ComputeHash(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string? Trunc(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Length <= max ? value : value[..max];
    }
}

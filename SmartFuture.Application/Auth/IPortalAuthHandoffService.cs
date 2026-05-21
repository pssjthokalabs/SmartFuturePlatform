using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Shared.Enums.Auth;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Auth;

public interface IPortalAuthHandoffService
{
    /// <summary>
    /// Issue a one-time portal-auth-handoff token for the given user.
    /// Returns the **raw** token (the only time it is ever exposed) plus
    /// the row id and expiry — caller is responsible for shipping the
    /// raw token to the user-agent and discarding it server-side.
    /// </summary>
    Task<IssuedHandoffToken> IssueAsync(Guid userId, string? intentToken, PortalAuthHandoffPurpose purpose,
        TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchange a raw handoff token for a normal login response. One-time
    /// use; consumed rows fail with a friendly error on retry.
    /// </summary>
    Task<Result<PortalAuthHandoffExchangeResponseDto>> ExchangeAsync(
        PortalAuthHandoffExchangeRequestDto request, CancellationToken cancellationToken = default);
}

public sealed record IssuedHandoffToken(string RawToken, Guid TokenId, DateTime ExpiresAtUtc);

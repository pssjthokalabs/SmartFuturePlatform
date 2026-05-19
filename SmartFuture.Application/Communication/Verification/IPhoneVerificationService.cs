using SmartFuture.Shared.Enums.Communication;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Communication.Verification;

/// <summary>
/// High-level phone-verification dispatcher. Used by future
/// "verify-phone" / "verify-with-OTP" flows. Wraps either Twilio
/// Verify (recommended — Twilio stores + validates the code for you)
/// or a fallback SMS-based code we manage ourselves.
///
/// Returns NOT_CONFIGURED when no provider is wired up. Concrete
/// Twilio implementation is deferred to a follow-up phase.
/// </summary>
public interface IPhoneVerificationService
{
    /// <summary>Start a verification — sends the code via the chosen channel.</summary>
    Task<Result<PhoneVerificationStartOutcome>> StartAsync(
        PhoneVerificationStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Check a user-supplied code against the live verification.</summary>
    Task<Result<PhoneVerificationCheckOutcome>> CheckAsync(
        PhoneVerificationCheckRequest request, CancellationToken cancellationToken = default);
}

public sealed record PhoneVerificationStartRequest(
    string ToPhoneNumber,
    MobileOtpChannel Channel,
    OtpPurpose Purpose,
    Guid? UserId = null,
    string? CorrelationId = null);

public sealed record PhoneVerificationStartOutcome(string ProviderName, string ProviderVerificationId);

public sealed record PhoneVerificationCheckRequest(
    string ToPhoneNumber,
    string Code,
    OtpPurpose Purpose,
    Guid? UserId = null);

public sealed record PhoneVerificationCheckOutcome(bool Approved, string ProviderName);

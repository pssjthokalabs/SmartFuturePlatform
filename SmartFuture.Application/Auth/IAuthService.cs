using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Auth;

public interface IAuthService
{
    Task<Result<AuthTokenDto>> RegisterAsync(RegisterRequestDto request);

    /// <summary>
    /// Password sign-in. Returns a <see cref="LoginOutcomeDto"/> wrapper that
    /// ALWAYS carries the verification state the API observed (EmailConfirmed,
    /// PhoneNumberConfirmed, AccountStatus, masked identifiers, available OTP
    /// channels). On success the wrapper's Token is populated; on every failure
    /// path the wrapper's diagnostic fields are still set so the caller can
    /// route on observed state without a second round-trip.
    /// </summary>
    Task<Result<LoginOutcomeDto>> LoginAsync(LoginRequestDto request);
    Task<Result<AuthTokenDto>> RefreshTokenAsync(RefreshTokenRequestDto request);
    Task<Result> RevokeRefreshTokenAsync(RefreshTokenRequestDto request);
    Task<Result> ForgotPasswordAsync(ForgotPasswordRequestDto request);
    Task<Result> ResetPasswordAsync(ResetPasswordRequestDto request);
    Task<Result> RequestChangePasswordCodeAsync(Guid userId, RequestChangePasswordCodeRequestDto request);
    Task<Result> ConfirmChangePasswordAsync(Guid userId, ConfirmChangePasswordRequestDto request);
    Task<Result<CurrentUserDto>> GetCurrentUserAsync(Guid userId);

    /// <summary>
    /// Check whether the supplied email is free for registration. Returns
    /// <see cref="CheckIdentifierAvailableResponseDto.Available"/> = true when
    /// no user holds the email (case-insensitive). Does NOT enumerate other
    /// user data.
    /// </summary>
    Task<Result<CheckIdentifierAvailableResponseDto>> IsEmailAvailableAsync(string? email);

    /// <summary>
    /// Check whether the supplied phone number is free for registration. The
    /// phone is normalised via <c>PhoneNumberNormalizer</c> so SA numbers in
    /// any common shape collide correctly. Returns
    /// <see cref="CheckIdentifierAvailableResponseDto.Available"/> = true when
    /// no user holds the normalised number. Does NOT enumerate other user
    /// data.
    /// </summary>
    Task<Result<CheckIdentifierAvailableResponseDto>> IsPhoneAvailableAsync(string? phoneNumber);

    /// <summary>
    /// TEMPORARY dev / UAT OTP-login bridge until a real OTP delivery
    /// provider is integrated. Accepts an email-or-phone identifier plus
    /// the static dev pin. When the environment is non-production AND the
    /// identifier resolves to an active user AND the pin matches the
    /// configured dev value, mints a normal <see cref="AuthTokenDto"/> so
    /// the mobile app can complete the OTP login flow against UAT.
    ///
    /// Hard-gated by <c>IHostEnvironment.IsProduction()</c>; will return a
    /// FORBIDDEN result on Live regardless of the pin.
    /// </summary>
    Task<Result<AuthTokenDto>> DevOtpLoginAsync(DevOtpLoginRequestDto request);

    Task<Result> RequestOtpAsync(OtpRequestDto request);
    Task<Result<AuthTokenDto>> VerifyOtpAsync(OtpVerifyDto request);

    /// <summary>
    /// Authenticated: send an OTP for post-registration account
    /// verification on the user's email or phone. Identifier comes from
    /// the JWT, NOT the request body, so the surface can't be abused for
    /// account enumeration.
    /// </summary>
    Task<Result> RequestAccountVerificationCodeAsync(Guid userId, RequestAccountVerificationCodeDto request);

    /// <summary>
    /// Authenticated: confirm an OTP and flip EmailConfirmed or
    /// PhoneNumberConfirmed depending on the channel. Honors the UAT
    /// super-OTP bypass when configured AND non-production — flips BOTH
    /// flags at once in that case. Returns the post-confirm flag state.
    /// </summary>
    Task<Result<AccountVerificationStatusDto>> ConfirmAccountVerificationAsync(Guid userId, ConfirmAccountVerificationDto request);
}

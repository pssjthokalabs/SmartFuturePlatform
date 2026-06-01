namespace SmartFuture.Application.Auth;

/// <summary>
/// OTP-related configuration for the post-registration account-verification
/// flow.
///
/// The UAT super OTP is a single code that — when configured and the host
/// environment is non-production — instantly marks BOTH EmailConfirmed AND
/// PhoneNumberConfirmed for the signed-in user. It exists ONLY so QA can
/// move past the verification gate without a real email/SMS provider in
/// the loop. The code is read from config at request time, never logged,
/// and hard-gated by <c>IHostEnvironment.IsProduction()</c> inside the
/// service. Even if the config flag is true in a Production deployment,
/// the production-environment guard rejects the bypass.
///
/// Bind from configuration via section <c>Otp</c>. Env-var form:
/// <c>Otp__UatSuperOtpEnabled=true</c> + <c>Otp__UatSuperOtpCode=650352</c>.
/// </summary>
public class OtpSettings
{
    public const string SectionName = "Otp";

    /// <summary>
    /// Master switch for the UAT super-OTP bypass. Default false — must
    /// be explicitly turned on per environment. Has no effect on Production
    /// regardless of value.
    /// </summary>
    public bool UatSuperOtpEnabled { get; set; } = false;

    /// <summary>
    /// The single super-OTP code QA can use to confirm an account in UAT.
    /// Compared in constant time. Empty string disables the bypass even
    /// when <see cref="UatSuperOtpEnabled"/> is true.
    /// </summary>
    public string UatSuperOtpCode { get; set; } = string.Empty;
}

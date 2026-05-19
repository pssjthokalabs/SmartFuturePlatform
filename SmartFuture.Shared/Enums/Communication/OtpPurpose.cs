namespace SmartFuture.Shared.Enums.Communication;

// Why an OTP / verification code is being issued. Used for audit
// trail + rate-limiting buckets (a user may have one outstanding
// PhoneVerification code while also having an open ChangePassword
// code, for example).
public enum OtpPurpose
{
    PhoneVerification = 0,
    EmailVerification = 1,
    PasswordReset = 2,
    LoginChallenge = 3,
    TransactionApproval = 4,
    ChangePassword = 5,
    ChangePhoneNumber = 6,
    ChangeEmail = 7
}

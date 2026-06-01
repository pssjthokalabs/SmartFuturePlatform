namespace SmartFuture.Shared.Enums.Identity;

public enum VerificationCodePurpose
{
    ChangePassword = 0,
    ChangePhoneNumber = 1,
    ChangeEmail = 2,
    // Phase 35C — forgot-password is now a 6-digit OTP flow that
    // reuses the same VerificationCodes table. The reset token link
    // path is gone; ResetPasswordAsync now takes (email, code,
    // newPassword) and validates against rows of this purpose.
    PasswordReset = 3,
    // Post-registration account verification. Customers who registered
    // with EmailConfirmed=false/PhoneNumberConfirmed=false get prompted
    // to verify one of the two channels before they can use the Client
    // Zone. Email codes go via the VerificationCodes table; SMS codes
    // go via Twilio Verify (no row stored — Twilio holds the code).
    AccountVerification = 4
}

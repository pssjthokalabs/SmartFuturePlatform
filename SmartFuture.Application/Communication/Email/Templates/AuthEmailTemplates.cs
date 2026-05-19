using System.Text;
using SmartFuture.Shared.Enums.Communication;

namespace SmartFuture.Application.Communication.Email.Templates;

/// <summary>
/// Auth / account-security email templates. All sent from the
/// <see cref="EmailSenderType.Security"/> identity by default
/// (typically noreply@smartfuture.co.za), keeping security mail
/// visually distinct from billing/support correspondence.
///
/// **OTP/code values are never logged**. Templates accept the live
/// code, render it once into the user-facing body, and the calling
/// service hands the rendered output to <c>INotificationService</c>
/// which only persists a body snippet (no full code).
/// </summary>
public static class AuthEmailTemplates
{
    /// <summary>
    /// Password-reset email containing a single-use link. Used by
    /// <c>AuthService.ForgotPasswordAsync</c>.
    /// </summary>
    public static SmartFutureEmailContent PasswordResetLink(string firstName, string emailAddress, string resetUrl, int expiryMinutes)
    {
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName;
        const string subject = "Reset your Smart Future password";

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: "Use the secure link below to set a new password. The link expires soon.",
            innerHtml: string.Concat(
                SmartFutureEmailLayout.Heading("Reset your password"),
                SmartFutureEmailLayout.Paragraph($"Hi {greeting},"),
                SmartFutureEmailLayout.Paragraph(
                    $"We received a request to reset the password for your Smart Future account ({emailAddress}). " +
                    "Click the button below to choose a new password. For your security, this link can only be used once."),
                SmartFutureEmailLayout.CallToActionButton(resetUrl, "Reset password"),
                SmartFutureEmailLayout.MutedNote(
                    expiryMinutes > 0
                        ? $"This link expires in about {expiryMinutes} minutes. If it expires, you can request a new one from the sign-in page."
                        : "This link expires soon. If it expires, you can request a new one from the sign-in page."),
                SmartFutureEmailLayout.Divider(),
                SmartFutureEmailLayout.MutedNote(
                    "If you didn't request a password reset, you can safely ignore this email — your password will not change. " +
                    "Reply to this thread if anything looks suspicious.")));

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine($"We received a request to reset the password for your Smart Future account ({emailAddress}).")
            .AppendLine("Use the link below to choose a new password. For your security, this link can only be used once:")
            .AppendLine()
            .AppendLine(resetUrl)
            .AppendLine()
            .AppendLine(expiryMinutes > 0
                ? $"This link expires in about {expiryMinutes} minutes."
                : "This link expires soon.")
            .AppendLine()
            .AppendLine("If you did not request a password reset, you can safely ignore this email — your password will not change.")
            .AppendLine()
            .AppendLine("— The Smart Future team")
            .ToString();

        return new SmartFutureEmailContent(EmailSenderType.Security, subject, html, plain);
    }

    /// <summary>
    /// Change-password verification code, sent to a signed-in user
    /// before they can rotate their password. Used by
    /// <c>AuthService.RequestChangePasswordCodeAsync</c>.
    /// </summary>
    public static SmartFutureEmailContent ChangePasswordCode(string firstName, string code, int expiryMinutes)
    {
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName;
        const string subject = "Your Smart Future password-change code";

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: "Enter the code in the portal to change your password.",
            innerHtml: string.Concat(
                SmartFutureEmailLayout.Heading("Change your password"),
                SmartFutureEmailLayout.Paragraph($"Hi {greeting},"),
                SmartFutureEmailLayout.Paragraph(
                    "Use the code below in the Smart Future portal to confirm your password change request:"),
                SmartFutureEmailLayout.CodeBlock(code),
                SmartFutureEmailLayout.MutedNote(
                    expiryMinutes > 0
                        ? $"This code expires in {expiryMinutes} minutes and can only be used once."
                        : "This code expires soon and can only be used once."),
                SmartFutureEmailLayout.Divider(),
                SmartFutureEmailLayout.MutedNote(
                    "If you didn't request this code, you can safely ignore this email — your password will not change.")));

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine("Use the verification code below in the Smart Future portal to change your password:")
            .AppendLine()
            .AppendLine($"    {code}")
            .AppendLine()
            .AppendLine(expiryMinutes > 0
                ? $"This code expires in {expiryMinutes} minutes and can only be used once."
                : "This code expires soon and can only be used once.")
            .AppendLine()
            .AppendLine("If you didn't request this code, you can safely ignore this email — your password will not change.")
            .AppendLine()
            .AppendLine("— The Smart Future team")
            .ToString();

        return new SmartFutureEmailContent(EmailSenderType.Security, subject, html, plain);
    }

    /// <summary>
    /// Email-verification code. Template only — wiring to a backend
    /// flow is pending (no email-verification endpoint exists yet).
    /// Future <c>AuthService.RequestEmailVerificationAsync</c> can
    /// use this directly.
    /// </summary>
    public static SmartFutureEmailContent EmailVerificationCode(string firstName, string code, int expiryMinutes)
    {
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName;
        const string subject = "Verify your Smart Future email";

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: "Enter the code in the portal to verify your email address.",
            innerHtml: string.Concat(
                SmartFutureEmailLayout.Heading("Verify your email"),
                SmartFutureEmailLayout.Paragraph($"Hi {greeting},"),
                SmartFutureEmailLayout.Paragraph(
                    "Welcome to Smart Future. Use the code below in the portal to confirm your email address:"),
                SmartFutureEmailLayout.CodeBlock(code),
                SmartFutureEmailLayout.MutedNote(
                    expiryMinutes > 0
                        ? $"This code expires in {expiryMinutes} minutes."
                        : "This code expires soon."),
                SmartFutureEmailLayout.Divider(),
                SmartFutureEmailLayout.MutedNote(
                    "If you didn't sign up for Smart Future, you can safely ignore this email.")));

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine("Use the code below to verify your email address on Smart Future:")
            .AppendLine()
            .AppendLine($"    {code}")
            .AppendLine()
            .AppendLine(expiryMinutes > 0
                ? $"This code expires in {expiryMinutes} minutes."
                : "This code expires soon.")
            .AppendLine()
            .AppendLine("— The Smart Future team")
            .ToString();

        return new SmartFutureEmailContent(EmailSenderType.Accounts, subject, html, plain);
    }
}

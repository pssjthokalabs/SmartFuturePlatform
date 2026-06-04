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
    /// Password-reset email containing a 6-digit OTP. **Primary
    /// forgot-password template from Phase 35C onwards.** The recipient
    /// enters the code on the reset-password page; the previous
    /// link-based flow is no longer used.
    /// </summary>
    public static SmartFutureEmailContent PasswordResetCode(string firstName, string code, int expiryMinutes)
    {
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName;
        const string subject = "Your Smart Future password reset code";

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: "Enter the code on the reset-password page to set a new password.",
            innerHtml: string.Concat(
                SmartFutureEmailLayout.Heading("Reset your password"),
                SmartFutureEmailLayout.Paragraph($"Hi {greeting},"),
                SmartFutureEmailLayout.Paragraph(
                    "Use the code below on the Smart Future password-reset page to set a new password:"),
                SmartFutureEmailLayout.CodeBlock(code),
                SmartFutureEmailLayout.MutedNote(
                    expiryMinutes > 0
                        ? $"This code expires in {expiryMinutes} minutes and can only be used once."
                        : "This code expires soon and can only be used once."),
                SmartFutureEmailLayout.Divider(),
                SmartFutureEmailLayout.MutedNote(
                    "If you didn't request a password reset, you can safely ignore this email — your password will not change.")));

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine("Use the code below on the Smart Future password-reset page to set a new password:")
            .AppendLine()
            .AppendLine($"    {code}")
            .AppendLine()
            .AppendLine(expiryMinutes > 0
                ? $"This code expires in {expiryMinutes} minutes and can only be used once."
                : "This code expires soon and can only be used once.")
            .AppendLine()
            .AppendLine("If you didn't request a password reset, you can safely ignore this email — your password will not change.")
            .AppendLine()
            .AppendLine("— The Smart Future team")
            .ToString();

        return new SmartFutureEmailContent(EmailSenderType.Security, subject, html, plain);
    }

    /// <summary>
    /// Legacy link-based password-reset email. **Deprecated by
    /// <see cref="PasswordResetCode"/> in Phase 35C.** Kept compiled
    /// for backwards compatibility — no current call sites use it.
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

    /// <summary>
    /// Welcome / account-created invite sent immediately after a
    /// SmartFuture admin creates a user from the portal. Carries the
    /// temporary password the admin set during creation + the correct
    /// sign-in URL for the new user's role (admin host for staff,
    /// Client Zone for customers).
    ///
    /// **Security note**: temporary passwords are sent by email by
    /// explicit business request — see the spec attached to Phase 56.
    /// The template tells the recipient to change the password after
    /// signing in. Callers MUST NOT log the password.
    /// </summary>
    public static SmartFutureEmailContent WelcomeUserInvite(
        string firstName,
        string emailAddress,
        string roleLabel,
        string temporaryPassword,
        string loginUrl)
    {
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName;
        const string subject = "Your SmartFuture account has been created";

        var html = SmartFutureEmailLayout.Compose(
            title: subject,
            preheader: "Sign in with the temporary password and change it after your first login.",
            innerHtml: string.Concat(
                SmartFutureEmailLayout.Heading("Welcome to SmartFuture"),
                SmartFutureEmailLayout.Paragraph($"Hi {greeting},"),
                SmartFutureEmailLayout.Paragraph(
                    $"A SmartFuture administrator has added you as a <strong>{System.Net.WebUtility.HtmlEncode(roleLabel)}</strong> user. " +
                    "You can sign in using the details below."),
                SmartFutureEmailLayout.KeyValueTable(new List<(string Label, string Value)>
                {
                    ("Sign-in URL",       loginUrl),
                    ("Email / username",  emailAddress ?? string.Empty),
                    ("Temporary password", temporaryPassword ?? string.Empty),
                }),
                SmartFutureEmailLayout.MutedNote(
                    "Please change this temporary password after your first sign-in. " +
                    "If you ever forget your password, use the \"Forgot password\" link on the sign-in page."),
                SmartFutureEmailLayout.Divider(),
                SmartFutureEmailLayout.MutedNote(
                    "If you didn't expect this email, please contact SmartFuture support — your account will remain protected by the password change.")));

        var plain = new StringBuilder()
            .AppendLine($"Hi {greeting},")
            .AppendLine()
            .AppendLine($"A SmartFuture administrator has added you as a {roleLabel} user. " +
                        "You can sign in using the details below.")
            .AppendLine()
            .AppendLine($"Sign-in URL:        {loginUrl}")
            .AppendLine($"Email / username:   {emailAddress}")
            .AppendLine($"Temporary password: {temporaryPassword}")
            .AppendLine()
            .AppendLine("Please change this temporary password after your first sign-in. " +
                        "If you ever forget your password, use the \"Forgot password\" link on the sign-in page.")
            .AppendLine()
            .AppendLine("If you didn't expect this email, please contact SmartFuture support — " +
                        "your account will remain protected by the password change.")
            .AppendLine()
            .AppendLine("— The Smart Future team")
            .ToString();

        return new SmartFutureEmailContent(EmailSenderType.Security, subject, html, plain);
    }
}

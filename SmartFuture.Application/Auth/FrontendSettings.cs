namespace SmartFuture.Application.Auth;

// Frontend URL hints used when the API needs to build outbound links
// (password-reset emails, welcome invite emails, etc.). Read from
// configuration so each environment can point at its own deployment
// without code changes.
//
// Defaults assume the SmartFuturePortal dev server. Override in
// `appsettings.Development.json` or via environment variables, e.g.:
//   FrontendSettings__AdminResetPasswordUrl=https://admin.smartfuture.co.za/reset-password
//   FrontendSettings__ClientResetPasswordUrl=https://clientzone.smartfuture.co.za/reset-password
//   FrontendSettings__AdminPortalLoginUrl=https://admin.smartfuture.co.za/login
//   FrontendSettings__ClientPortalLoginUrl=https://clientzone.smartfuture.co.za/login
public class FrontendSettings
{
    public const string SectionName = "FrontendSettings";

    public string AdminResetPasswordUrl  { get; set; } = "http://localhost:5173/admin/reset-password";
    public string ClientResetPasswordUrl { get; set; } = "http://localhost:5173/client/reset-password";

    // Phase 56 — sign-in URLs used by the "welcome / account created"
    // email the API sends when an admin creates a user from the portal.
    // The choice between admin and client URL is driven by the new
    // user's role (Admin/Technician/Support land on the admin portal;
    // Customer lands on Client Zone).
    public string AdminPortalLoginUrl    { get; set; } = "http://localhost:5173/admin/login";
    public string ClientPortalLoginUrl   { get; set; } = "http://localhost:5173/client/login";
}

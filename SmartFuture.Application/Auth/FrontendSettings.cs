namespace SmartFuture.Application.Auth;

// Frontend URL hints used when the API needs to build outbound links
// (password-reset emails, etc.). These are read from configuration so
// each environment can point at its own deployment without code changes.
//
// Defaults assume the SmartFuturePortal dev server. Override in
// `appsettings.Development.json` or via environment variables, e.g.:
//   FrontendSettings__AdminResetPasswordUrl=https://admin.smartfuture.co.za/admin/reset-password
//   FrontendSettings__ClientResetPasswordUrl=https://portal.smartfuture.co.za/client/reset-password
public class FrontendSettings
{
    public const string SectionName = "FrontendSettings";

    public string AdminResetPasswordUrl { get; set; } = "http://localhost:5173/admin/reset-password";
    public string ClientResetPasswordUrl { get; set; } = "http://localhost:5173/client/reset-password";
}

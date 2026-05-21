namespace SmartFuture.Shared.Enums.Auth;

// Why a one-time portal-auth-handoff token was issued. Currently only
// the website registration flow produces them, but other handoffs
// (admin impersonation, magic-link login) would extend this enum.
public enum PortalAuthHandoffPurpose
{
    WebsiteRegistration = 0,
}

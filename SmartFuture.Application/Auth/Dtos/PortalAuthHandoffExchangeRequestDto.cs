namespace SmartFuture.Application.Auth.Dtos;

// Body for POST /api/auth/portal-handoff/exchange. The portal sends
// the raw token (received in a query string from the website) back as
// JSON so it never lands in our access logs.
public class PortalAuthHandoffExchangeRequestDto
{
    public string Token { get; set; } = string.Empty;

    // Optional. The intent token the website also attached to the
    // handoff URL — surfaced back on the response so the portal can
    // route to `/client/orders/new?intentToken=…` without trusting the
    // URL query string a second time.
    public string? IntentToken { get; set; }
}

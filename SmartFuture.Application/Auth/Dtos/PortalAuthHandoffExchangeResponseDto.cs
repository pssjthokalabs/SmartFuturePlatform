namespace SmartFuture.Application.Auth.Dtos;

// Wrapper around the normal login response so the portal handoff route
// can also know which intent it should land the user on without re-
// reading the URL.
public class PortalAuthHandoffExchangeResponseDto
{
    public AuthTokenDto Auth { get; set; } = new();
    public string? IntentToken { get; set; }
}

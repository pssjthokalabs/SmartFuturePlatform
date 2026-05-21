using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Shared.Enums.OrderIntents;

namespace SmartFuture.Application.OrderIntents.Dtos;

// Anonymous preview returned by GET /api/public/order-intents/{token}.
// Designed to let the unauthenticated register/login page show "you
// selected the 100/50 Mbps Uncapped Fibre package" without revealing
// any other visitor's PII.
//
// Deliberately limits the visible address/contact slice — we return the
// city/province (geographic context) but NOT the street address or the
// phone number. The full record is only visible after the user
// authenticates and claims the intent.
public class PublicOrderIntentPreviewDto
{
    public string IntentToken { get; set; } = string.Empty;
    public OrderIntentStatus Status { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    public ServicePackageDto? Package { get; set; }

    // Prefill hints. Email is returned so the register form can pre-fill
    // it — it's already what the visitor typed and the user-visible URL
    // already proves they have the token, so disclosing it back is
    // information they themselves provided.
    public string? Email { get; set; }
    public string? FullName { get; set; }

    public string? City { get; set; }
    public string? Province { get; set; }
}
